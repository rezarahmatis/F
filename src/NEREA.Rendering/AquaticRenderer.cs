using System.Numerics;
using System.Runtime.CompilerServices;
using NEREA.Core.Content;
using NEREA.Core.Engine.FishSimulation;
using NEREA.Core.Models;
using NEREA.Core.Utilities;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NEREA.Rendering;

/// <summary>
/// The DirectX 12 aquatic renderer. Frame structure:
///   1. HDR (optionally MSAA) scene target: background volume, plants, shadows, reflections, creatures, particles
///   2. MSAA resolve
///   3. post pass into the swap chain: ripple refraction, bloom, grade
/// Per-frame data lives in persistently mapped upload ring buffers (two frames in flight); nothing is allocated per frame.
/// </summary>
public sealed unsafe class AquaticRenderer : IDisposable
{
    private const int FrameCount = 2;
    private const Format BackFormat = Format.B8G8R8A8_UNorm;
    private const Format SceneFormat = PipelineSet.SceneFormat;
    private const Format DepthFormat = PipelineSet.DepthFormat;
    private const int MaxAgents = EcosystemSimulation.MaxAgents;
    private const int InstanceStride = 64;
    private const int MaxSpeciesGpu = 64;

    private sealed class FrameResource
    {
        public ID3D12CommandAllocator Allocator = null!;
        public ID3D12Resource ConstantBuffer = null!;
        public byte* ConstantPtr;
        public ID3D12Resource InstanceBuffer = null!;
        public byte* InstancePtr;
        public ulong FenceValue;
    }

    private sealed class GpuMesh : IDisposable
    {
        public ID3D12Resource VertexBuffer = null!;
        public ID3D12Resource IndexBuffer = null!;
        public uint IndexCount;
        public VertexBufferView VertexView;
        public IndexBufferView IndexView;

        public void Dispose() { VertexBuffer?.Dispose(); IndexBuffer?.Dispose(); }
    }

    private readonly GpuDevice gpu;
    private readonly ID3D12Device device;
    private readonly ID3D12RootSignature rootSignature;
    private readonly ID3D12PipelineState postPipeline;
    private readonly Dictionary<int, PipelineSet> pipelineSets = new();

    private IDXGISwapChain3 swapChain = null!;
    private readonly ID3D12Resource?[] backBuffers = new ID3D12Resource?[FrameCount];
    private readonly FrameResource[] frames = new FrameResource[FrameCount];
    private readonly ID3D12GraphicsCommandList cmd;
    private int frameIndex;

    private readonly ID3D12DescriptorHeap rtvHeap;
    private readonly ID3D12DescriptorHeap dsvHeap;
    private readonly ID3D12DescriptorHeap srvHeap;
    private readonly uint rtvStep;

    private ID3D12Resource? sceneColor;
    private ID3D12Resource? sceneResolved;
    private ID3D12Resource? sceneDepth;
    private ResourceStates sceneState;
    private int sceneWidth, sceneHeight, sceneSamples;

    private readonly GpuMesh fishMesh, rayMesh, jellyMesh, plantMesh;
    private ID3D12Resource? plantBuffer;
    private uint plantCount;
    private readonly ID3D12Resource speciesBuffer;
    private byte* speciesPtr;

    private int backWidth = 1280, backHeight = 720;
    private float compositionScaleX = 1f, compositionScaleY = 1f;
    private readonly object resizeLock = new();
    private bool resizePending;
    private bool deviceLost;

    private FrameConstants constants;
    private readonly int[] planCounts = new int[4];
    private readonly int[] planOffsets = new int[4];
    private readonly VertexBufferView[] vbViews = new VertexBufferView[2];
    private readonly Vector3[] particleDummy = Array.Empty<Vector3>();

    // Per-world static GPU constants
    private WorldDefinition? world;
    private float wallDistance;

    public string AdapterName => gpu.AdapterName;
    public bool IsSoftwareAdapter => gpu.IsSoftware;
    public bool IsLost => deviceLost;
    public IntPtr SwapChainPointer => swapChain.NativePointer;
    public int SceneWidth => sceneWidth;
    public int SceneHeight => sceneHeight;

    public AquaticRenderer(bool preferLowPower)
    {
        gpu = GpuDevice.Create(preferLowPower);
        device = gpu.Device;
        try
        {
            rootSignature = RootSignatures.Create(device);
            postPipeline = PipelineSet.CreatePost(device, rootSignature, BackFormat);

            rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount + 1, DescriptorHeapFlags.None, 0));
            dsvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1, DescriptorHeapFlags.None, 0));
            srvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible, 0));
            rtvStep = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            for (int i = 0; i < FrameCount; i++)
            {
                var fr = new FrameResource
                {
                    Allocator = device.CreateCommandAllocator(CommandListType.Direct),
                    ConstantBuffer = gpu.CreateUploadBuffer(1024),
                    InstanceBuffer = gpu.CreateUploadBuffer((ulong)(4 * MaxAgents * InstanceStride))
                };
                void* p;
                fr.ConstantBuffer.Map(0, &p); fr.ConstantPtr = (byte*)p;
                fr.InstanceBuffer.Map(0, &p); fr.InstancePtr = (byte*)p;
                frames[i] = fr;
            }

            cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, frames[0].Allocator, null);
            cmd.Close();

            speciesBuffer = gpu.CreateUploadBuffer((ulong)(MaxSpeciesGpu * 6 * 16));
            void* sp;
            speciesBuffer.Map(0, &sp); speciesPtr = (byte*)sp;

            fishMesh = Upload(MeshBuilder.Fish());
            rayMesh = Upload(MeshBuilder.Ray());
            jellyMesh = Upload(MeshBuilder.Jelly());
            plantMesh = Upload(MeshBuilder.Plant());

            CreateSwapChain();
            GetOrCreatePipelines(1);
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    // ───────────────────────────── creation helpers ─────────────────────────────

    private GpuMesh Upload(MeshData data)
    {
        var mesh = new GpuMesh { IndexCount = (uint)data.Indices.Count };
        uint vbSize = (uint)(data.Vertices.Count * 16);
        uint ibSize = (uint)(data.Indices.Count * 4);

        mesh.VertexBuffer = gpu.CreateUploadBuffer(vbSize);
        mesh.IndexBuffer = gpu.CreateUploadBuffer(ibSize);

        void* p;
        mesh.VertexBuffer.Map(0, &p);
        for (int i = 0; i < data.Vertices.Count; i++) ((Vector4*)p)[i] = data.Vertices[i];
        mesh.VertexBuffer.Unmap(0);

        mesh.IndexBuffer.Map(0, &p);
        for (int i = 0; i < data.Indices.Count; i++) ((uint*)p)[i] = data.Indices[i];
        mesh.IndexBuffer.Unmap(0);

        mesh.VertexView = new VertexBufferView(mesh.VertexBuffer.GPUVirtualAddress, vbSize, 16);
        mesh.IndexView = new IndexBufferView(mesh.IndexBuffer.GPUVirtualAddress, ibSize, true);
        return mesh;
    }

    private PipelineSet GetOrCreatePipelines(int samples)
    {
        if (!pipelineSets.TryGetValue(samples, out var set))
        {
            set = new PipelineSet(device, rootSignature, samples);
            pipelineSets[samples] = set;
        }
        return set;
    }

    private void CreateSwapChain()
    {
        var desc = new SwapChainDescription1
        {
            Width = (uint)backWidth,
            Height = (uint)backHeight,
            Format = BackFormat,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = FrameCount,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Stereo = false
        };
        using var sc1 = gpu.Factory.CreateSwapChainForComposition(gpu.Queue, desc, null);
        swapChain = sc1.QueryInterface<IDXGISwapChain3>();
        ApplyCompositionTransform();
        CreateBackBufferViews();
    }

    private void ApplyCompositionTransform()
    {
        try
        {
            using var sc2 = swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (sc2 != null) sc2.MatrixTransform = Matrix3x2.CreateScale(1f / compositionScaleX, 1f / compositionScaleY);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not apply swap chain composition transform.", ex);
        }
    }

    private CpuDescriptorHandle Rtv(int index)
    {
        var h = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        h.Ptr += (nuint)(index * rtvStep);
        return h;
    }

    private void CreateBackBufferViews()
    {
        for (int i = 0; i < FrameCount; i++)
        {
            backBuffers[i]?.Dispose();
            backBuffers[i] = swapChain.GetBuffer<ID3D12Resource>((uint)i);
            device.CreateRenderTargetView(backBuffers[i]!, null, Rtv(i));
        }
    }

    private void CreateSceneTargets(int width, int height, int samples)
    {
        gpu.WaitIdle();
        sceneColor?.Dispose(); sceneResolved?.Dispose(); sceneDepth?.Dispose();
        sceneResolved = null;
        sceneWidth = width; sceneHeight = height; sceneSamples = samples;
        bool msaa = samples > 1;

        var clear = new ClearValue(SceneFormat, new Color4(0, 0, 0, 1));
        sceneColor = device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(SceneFormat, (uint)width, (uint)height, 1, 1, (uint)samples, 0, ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget, clear);
        sceneState = ResourceStates.RenderTarget;
        device.CreateRenderTargetView(sceneColor, null, Rtv(FrameCount));

        if (msaa)
        {
            sceneResolved = device.CreateCommittedResource(HeapType.Default,
                ResourceDescription.Texture2D(SceneFormat, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.None),
                ResourceStates.PixelShaderResource, null);
        }

        var depthClear = new ClearValue(DepthFormat, 1f, 0);
        sceneDepth = device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(DepthFormat, (uint)width, (uint)height, 1, 1, (uint)samples, 0, ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite, depthClear);
        device.CreateDepthStencilView(sceneDepth, null, dsvHeap.GetCPUDescriptorHandleForHeapStart());

        var srv = new ShaderResourceViewDescription
        {
            Format = SceneFormat,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
        };
        device.CreateShaderResourceView(sceneResolved ?? sceneColor, srv, srvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    // ───────────────────────────── public API ─────────────────────────────

    /// <summary>Sets the swap chain size in physical pixels and the XAML composition scale. Safe from any thread.</summary>
    public void Resize(int pixelWidth, int pixelHeight, float scaleX, float scaleY)
    {
        lock (resizeLock)
        {
            backWidth = Math.Max(64, pixelWidth);
            backHeight = Math.Max(64, pixelHeight);
            compositionScaleX = Math.Max(0.25f, scaleX);
            compositionScaleY = Math.Max(0.25f, scaleY);
            resizePending = true;
        }
    }

    /// <summary>Uploads the species style table and the vegetation of a world. Call from the render thread.</summary>
    public void LoadWorld(WorldDefinition newWorld, IReadOnlyList<SpeciesDefinition> species, float plantDensity)
    {
        gpu.WaitIdle();
        world = newWorld;
        wallDistance = PlantScatter.WallDistance(newWorld);

        // species style table
        var table = (Vector4*)speciesPtr;
        for (int i = 0; i < MaxSpeciesGpu * 6; i++) table[i] = Vector4.Zero;
        for (int i = 0; i < Math.Min(species.Count, MaxSpeciesGpu); i++)
        {
            var s = species[i];
            float plan = s.Plan switch { BodyPlan.Fish => 0f, BodyPlan.Ray => 1f, BodyPlan.Jelly => 2f, _ => 3f };
            table[i * 6 + 0] = new Vector4(s.PrimaryColor, (float)s.Pattern);
            table[i * 6 + 1] = new Vector4(s.SecondaryColor, s.Glow);
            table[i * 6 + 2] = new Vector4(s.AccentColor, s.PatternScale);
            table[i * 6 + 3] = new Vector4(s.Height, s.Width, s.Tail, s.Dorsal);
            table[i * 6 + 4] = new Vector4(s.Peak, s.Fork, s.Snout, s.Hammer);
            table[i * 6 + 5] = new Vector4(s.Curl, s.Legs, s.Flap, plan);
        }

        // vegetation
        plantBuffer?.Dispose();
        plantBuffer = null;
        var plants = PlantScatter.Build(newWorld, plantDensity);
        plantCount = (uint)plants.Length;
        if (plants.Length > 0)
        {
            plantBuffer = gpu.CreateUploadBuffer((ulong)(plants.Length * InstanceStride));
            void* p;
            plantBuffer.Map(0, &p);
            for (int i = 0; i < plants.Length; i++) ((PlantInstance*)p)[i] = plants[i];
            plantBuffer.Unmap(0);
        }
    }

    public void Render(RenderFrame f, ReadOnlySpan<FishAgent> agents, IReadOnlyList<SpeciesDefinition> species)
    {
        if (deviceLost || world == null) return;

        try
        {
            ApplyPendingResize();
            EnsureSceneTargets(f.Quality);

            var fr = frames[frameIndex];
            gpu.WaitFor(fr.FenceValue);
            fr.Allocator.Reset();
            cmd.Reset(fr.Allocator, null);

            WriteConstants(fr, f);
            WriteInstances(fr, agents, species, f.Quality.FishScale);
            Record(fr, f);
            cmd.Close();
            gpu.Queue.ExecuteCommandLists(new ID3D12CommandList[] { cmd });

            var present = swapChain.Present(1, PresentFlags.None);
            if (present == Vortice.DXGI.ResultCode.DeviceRemoved || present == Vortice.DXGI.ResultCode.DeviceReset)
            {
                deviceLost = true;
                Log.Error("Graphics device was removed.");
                return;
            }
            fr.FenceValue = gpu.Signal();
            frameIndex = (frameIndex + 1) % FrameCount;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            deviceLost = true;
            Log.Error("Graphics device error while rendering.", ex);
        }
    }

    // ───────────────────────────── per frame ─────────────────────────────

    private void ApplyPendingResize()
    {
        int w, h; float sx, sy;
        lock (resizeLock)
        {
            if (!resizePending) return;
            resizePending = false;
            w = backWidth; h = backHeight; sx = compositionScaleX; sy = compositionScaleY;
        }

        gpu.WaitIdle();
        for (int i = 0; i < FrameCount; i++) { backBuffers[i]?.Dispose(); backBuffers[i] = null; }
        swapChain.ResizeBuffers(FrameCount, (uint)w, (uint)h, Format.Unknown, SwapChainFlags.None);
        ApplyCompositionTransform();
        CreateBackBufferViews();
        frameIndex = (int)swapChain.CurrentBackBufferIndex;
        for (int i = 0; i < FrameCount; i++) frames[i].FenceValue = 0;
        sceneWidth = 0; // force scene target recreation at the new size
    }

    private void EnsureSceneTargets(in NEREA.Core.Engine.Quality.RenderQuality q)
    {
        int w = Math.Max(320, (int)(backWidth * q.RenderScale)) & ~1;
        int h = Math.Max(180, (int)(backHeight * q.RenderScale)) & ~1;
        int samples = q.Msaa ? 4 : 1;
        if (w == sceneWidth && h == sceneHeight && samples == sceneSamples && sceneColor != null) return;
        CreateSceneTargets(w, h, samples);
        GetOrCreatePipelines(samples);
    }

    private void WriteConstants(FrameResource fr, RenderFrame f)
    {
        var w = world!;
        var cam = f.Camera;
        var music = f.Music;
        var q = f.Quality;

        // Music colours the whole atmosphere very gently: dark music deepens the water, bright music lifts it
        float tone = music.IsPlaying ? 0.82f + 0.26f * music.Brightness : 1f;
        float depth = Math.Clamp(w.Depth + (music.IsPlaying ? (0.5f - music.Brightness) * 0.25f : 0f), 0f, 1f);
        float light = w.LightIntensity * f.LightScale * tone;

        float tanHalf = MathF.Tan(cam.FovY * 0.5f);
        constants = new FrameConstants
        {
            ViewProj = cam.ViewProjection,
            InvViewProj = cam.InverseViewProjection,
            CamPos = new Vector4(cam.Position, f.Time),
            Screen = new Vector4(sceneWidth, sceneHeight, 1f / (tanHalf * cam.Aspect), 1f / tanHalf),
            Music = new Vector4(music.Bass, music.Mid, music.Treble, music.Energy),
            Music2 = new Vector4(music.Beat, music.Calm, music.Brightness, music.Activity),
            SurfaceCol = new Vector4(w.SurfaceColor, w.SurfaceY),
            DeepCol = new Vector4(w.DeepColor, w.FloorY),
            FogCol = new Vector4(w.FogColor, w.Turbidity),
            LightCol = new Vector4(w.LightColor, light),
            SandCol = new Vector4(w.SandColor, w.FloorSlope),
            RockCol = new Vector4(w.RockColor, w.Enclosure),
            Water = new Vector4(w.Rays, w.Caustics, depth, w.Current),
            Grade = new Vector4(w.Exposure, w.Saturation, w.Vignette, (float)(int)w.Ceiling),
            ParticleCol = new Vector4(w.ParticleTint, w.ParticleGlow),
            Quality = new Vector4(q.WaterQuality, q.ParticleScale, f.ReducedMotion ? 0.15f : 1f, q.Bloom ? 1f : 0f),
            Misc = new Vector4(f.RippleCount, q.Reflections ? 1f : 0f, q.Shadows ? 1f : 0f, w.Bubbles),
            Extra = new Vector4(f.Fade, wallDistance, 0, 0)
        };

        *(FrameConstants*)fr.ConstantPtr = constants;
        var ripples = (Vector4*)(fr.ConstantPtr + sizeof(FrameConstants));
        for (int i = 0; i < RenderFrame.MaxRipples; i++) ripples[i] = f.Ripples[i];
    }

    private static bool Included(int index, float scale) =>
        scale >= 0.999f || ((index * 0.6180339887f) % 1f) < scale;

    private void WriteInstances(FrameResource fr, ReadOnlySpan<FishAgent> agents, IReadOnlyList<SpeciesDefinition> species, float fishScale)
    {
        Array.Clear(planCounts);
        for (int plan = 0; plan < 4; plan++) planOffsets[plan] = plan * MaxAgents;

        for (int i = 0; i < agents.Length; i++)
        {
            if (!Included(i, fishScale)) continue;
            ref readonly var a = ref agents[i];
            int plan = (int)species[a.Species].Plan;
            int slot = planOffsets[plan] + planCounts[plan]++;
            var inst = (CreatureInstance*)(fr.InstancePtr + (long)slot * InstanceStride);
            inst->PosLength = new Vector4(a.Position, a.Size);
            inst->ForwardPhase = new Vector4(a.Forward, a.Phase);
            inst->UpSwim = new Vector4(a.Up, a.Intensity);
            inst->Misc = new Vector4(a.Species, a.Bend, a.Seed, a.GlowPhase);
        }
    }

    private void SetPass(uint pass, uint particleType)
    {
        Span<uint> c = stackalloc uint[4];
        c[0] = pass; c[1] = particleType; c[2] = 0; c[3] = 0;
        cmd.SetGraphicsRoot32BitConstants(RootSignatures.DrawConstants, (ReadOnlySpan<uint>)c, 0);
    }

    private void DrawCreatures(FrameResource fr, int plan, ID3D12PipelineState pso, uint pass)
    {
        int n = planCounts[plan];
        if (n == 0) return;
        var mesh = plan == 0 ? fishMesh : plan == 1 ? rayMesh : jellyMesh;
        cmd.SetPipelineState(pso);
        SetPass(pass, 0);
        vbViews[0] = mesh.VertexView;
        vbViews[1] = new VertexBufferView(fr.InstanceBuffer.GPUVirtualAddress + (ulong)(planOffsets[plan] * InstanceStride),
            (uint)(n * InstanceStride), InstanceStride);
        cmd.IASetVertexBuffers(0, vbViews);
        cmd.IASetIndexBuffer(mesh.IndexView);
        cmd.DrawIndexedInstanced(mesh.IndexCount, (uint)n, 0, 0, 0);
    }

    private void Record(FrameResource fr, RenderFrame f)
    {
        var set = GetOrCreatePipelines(sceneSamples);
        var q = f.Quality;
        var back = backBuffers[frameIndex = (int)swapChain.CurrentBackBufferIndex]!;

        // ── scene pass ──
        if (sceneState != ResourceStates.RenderTarget)
        {
            cmd.ResourceBarrierTransition(sceneColor!, sceneState, ResourceStates.RenderTarget);
            sceneState = ResourceStates.RenderTarget;
        }
        var rtv = Rtv(FrameCount);
        var dsv = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        cmd.OMSetRenderTargets(rtv, dsv);
        cmd.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        cmd.ClearDepthStencilView(dsv, ClearFlags.Depth, 1f, 0);
        cmd.RSSetViewport(0, 0, sceneWidth, sceneHeight, 0f, 1f);
        cmd.RSSetScissorRect(sceneWidth, sceneHeight);
        cmd.SetGraphicsRootSignature(rootSignature);
        cmd.SetGraphicsRootConstantBufferView(RootSignatures.FrameCbv, fr.ConstantBuffer.GPUVirtualAddress);
        cmd.SetGraphicsRootConstantBufferView(RootSignatures.SpeciesCbv, speciesBuffer.GPUVirtualAddress);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // water volume
        cmd.SetPipelineState(set.Background);
        SetPass(0, 0);
        cmd.DrawInstanced(3, 1, 0, 0);

        // vegetation
        if (plantBuffer != null && plantCount > 0)
        {
            cmd.SetPipelineState(set.Plant);
            SetPass(0, 0);
            vbViews[0] = plantMesh.VertexView;
            vbViews[1] = new VertexBufferView(plantBuffer.GPUVirtualAddress, plantCount * InstanceStride, InstanceStride);
            cmd.IASetVertexBuffers(0, vbViews);
            cmd.IASetIndexBuffer(plantMesh.IndexView);
            cmd.DrawIndexedInstanced(plantMesh.IndexCount, plantCount, 0, 0, 0);
        }

        // soft floor shadows
        if (q.Shadows)
        {
            DrawCreatures(fr, 0, set.FishGhost, 1);
            DrawCreatures(fr, 1, set.RayGhost, 1);
            DrawCreatures(fr, 3, set.CephGhost, 1);
        }

        // mirror images seen against the underside of the surface
        if (q.Reflections)
        {
            DrawCreatures(fr, 0, set.FishGhost, 2);
            DrawCreatures(fr, 1, set.RayGhost, 2);
            DrawCreatures(fr, 3, set.CephGhost, 2);
            DrawCreatures(fr, 2, set.Jelly, 2);
        }

        // creatures
        DrawCreatures(fr, 0, set.FishMain, 0);
        DrawCreatures(fr, 1, set.RayMain, 0);
        DrawCreatures(fr, 3, set.CephMain, 0);
        DrawCreatures(fr, 2, set.Jelly, 0);

        // particles (GPU driven, no vertex buffers)
        cmd.SetPipelineState(set.Particles);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        float scale = q.ParticleScale * (0.85f + 0.3f * f.Music.Energy);
        DrawParticles(0, (int)(2200f * world!.Particles * scale), 5000);
        DrawParticles(1, (int)(700f * world.ParticleGlow * scale), 2500);
        DrawParticles(2, (int)(220f * world.Bubbles * scale), 800);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // ── resolve ──
        ID3D12Resource sceneSrv;
        if (sceneSamples > 1)
        {
            cmd.ResourceBarrierTransition(sceneColor!, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            cmd.ResourceBarrierTransition(sceneResolved!, ResourceStates.PixelShaderResource, ResourceStates.ResolveDest);
            cmd.ResolveSubresource(sceneResolved!, 0, sceneColor!, 0, SceneFormat);
            cmd.ResourceBarrierTransition(sceneResolved!, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
            sceneState = ResourceStates.ResolveSource;
            sceneSrv = sceneResolved!;
        }
        else
        {
            cmd.ResourceBarrierTransition(sceneColor!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            sceneState = ResourceStates.PixelShaderResource;
            sceneSrv = sceneColor!;
        }

        // ── post pass into the back buffer ──
        cmd.ResourceBarrierTransition(back, ResourceStates.Present, ResourceStates.RenderTarget);
        var backRtv = Rtv(frameIndex);
        cmd.OMSetRenderTargets(backRtv, null);
        cmd.RSSetViewport(0, 0, backWidth, backHeight, 0f, 1f);
        cmd.RSSetScissorRect(backWidth, backHeight);
        cmd.SetDescriptorHeaps(srvHeap);
        cmd.SetGraphicsRootDescriptorTable(RootSignatures.SceneTable, srvHeap.GetGPUDescriptorHandleForHeapStart());
        cmd.SetPipelineState(postPipeline);
        SetPass(0, 0);
        cmd.DrawInstanced(3, 1, 0, 0);
        cmd.ResourceBarrierTransition(back, ResourceStates.RenderTarget, ResourceStates.Present);
        _ = sceneSrv;
    }

    private void DrawParticles(uint type, int count, int max)
    {
        count = Math.Clamp(count, 0, max);
        if (count == 0) return;
        SetPass(0, type);
        cmd.DrawInstanced(4, (uint)count, 0, 0);
    }

    // ───────────────────────────── disposal ─────────────────────────────

    public void Dispose()
    {
        try { gpu.WaitIdle(); } catch { /* lost device */ }

        foreach (var set in pipelineSets.Values) set.Dispose();
        pipelineSets.Clear();
        postPipeline?.Dispose();
        rootSignature?.Dispose();

        plantBuffer?.Dispose();
        speciesBuffer?.Dispose();
        fishMesh?.Dispose(); rayMesh?.Dispose(); jellyMesh?.Dispose(); plantMesh?.Dispose();

        sceneColor?.Dispose(); sceneResolved?.Dispose(); sceneDepth?.Dispose();
        for (int i = 0; i < FrameCount; i++)
        {
            backBuffers[i]?.Dispose();
            var fr = frames[i];
            if (fr == null) continue;
            fr.ConstantBuffer?.Dispose();
            fr.InstanceBuffer?.Dispose();
            fr.Allocator?.Dispose();
        }
        cmd?.Dispose();
        rtvHeap?.Dispose(); dsvHeap?.Dispose(); srvHeap?.Dispose();
        swapChain?.Dispose();
        gpu.Dispose();
    }
}

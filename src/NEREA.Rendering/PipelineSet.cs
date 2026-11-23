using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace NEREA.Rendering;

/// <summary>Root signature shared by every pass.</summary>
internal static class RootSignatures
{
    public const uint FrameCbv = 0;       // b0
    public const uint DrawConstants = 1;  // b1 (4 x uint)
    public const uint SpeciesCbv = 2;     // b2
    public const uint SceneTable = 3;     // t0

    public static ID3D12RootSignature Create(ID3D12Device device)
    {
        var parameters = new[]
        {
            new RootParameter(RootParameterType.ConstantBufferView, new RootDescriptor(0, 0), ShaderVisibility.All),
            new RootParameter(new RootConstants(1, 0, 4), ShaderVisibility.All),
            new RootParameter(RootParameterType.ConstantBufferView, new RootDescriptor(2, 0), ShaderVisibility.All),
            new RootParameter(new RootDescriptorTable(new[]
            {
                new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0, 0, 0xFFFFFFFF)
            }), ShaderVisibility.Pixel)
        };
        var sampler = new StaticSamplerDescription(0, Filter.MinMagMipLinear,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            0f, 1, ComparisonFunction.Never, StaticBorderColor.TransparentBlack, 0f, float.MaxValue,
            ShaderVisibility.Pixel, 0);

        var desc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, new[] { sampler });
        return device.CreateRootSignature(desc, RootSignatureVersion.Version10);
    }
}

/// <summary>All pipeline state objects for one scene sample count (1 or 4 for MSAA).</summary>
internal sealed class PipelineSet : IDisposable
{
    public ID3D12PipelineState Background { get; }
    public ID3D12PipelineState Plant { get; }
    public ID3D12PipelineState FishMain { get; }
    public ID3D12PipelineState FishGhost { get; }
    public ID3D12PipelineState RayMain { get; }
    public ID3D12PipelineState RayGhost { get; }
    public ID3D12PipelineState CephMain { get; }
    public ID3D12PipelineState CephGhost { get; }
    public ID3D12PipelineState Jelly { get; }
    public ID3D12PipelineState Particles { get; }

    private readonly List<IDisposable> owned = new();

    public const Format SceneFormat = Format.R16G16B16A16_Float;
    public const Format DepthFormat = Format.D32_Float;

    private static readonly InputElementDescription[] CreatureLayout =
    {
        new("MESH", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerVertexData, 0),
        new("IPOS", 0, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
        new("IFWD", 0, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
        new("IUP", 0, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
        new("IMISC", 0, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1)
    };

    private static readonly InputElementDescription[] PlantLayout =
    {
        new("MESH", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerVertexData, 0),
        new("IPOS", 0, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
        new("IPAR", 0, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
        new("ICOLA", 0, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
        new("ICOLB", 0, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1)
    };

    public PipelineSet(ID3D12Device device, ID3D12RootSignature root, int samples)
    {
        byte[] vsBg = ShaderLibrary.Compile("Background.hlsl", "VSBackground", "vs_5_0");
        byte[] psBg = ShaderLibrary.Compile("Background.hlsl", "PSBackground", "ps_5_0");
        byte[] vsFish = ShaderLibrary.Compile("Fish.hlsl", "VSFish", "vs_5_0");
        byte[] vsRay = ShaderLibrary.Compile("Fish.hlsl", "VSRay", "vs_5_0");
        byte[] vsJelly = ShaderLibrary.Compile("Fish.hlsl", "VSJelly", "vs_5_0");
        byte[] psCreature = ShaderLibrary.Compile("Fish.hlsl", "PSCreature", "ps_5_0");
        byte[] psJelly = ShaderLibrary.Compile("Fish.hlsl", "PSJelly", "ps_5_0");
        byte[] vsPlant = ShaderLibrary.Compile("Plants.hlsl", "VSPlant", "vs_5_0");
        byte[] psPlant = ShaderLibrary.Compile("Plants.hlsl", "PSPlant", "ps_5_0");
        byte[] vsPart = ShaderLibrary.Compile("Particles.hlsl", "VSParticle", "vs_5_0");
        byte[] psPart = ShaderLibrary.Compile("Particles.hlsl", "PSParticle", "ps_5_0");

        var opaqueDepth = new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual);
        var readDepth = new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual);
        var noDepth = new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always);

        var a2c = BlendDescription.Opaque;
        a2c.AlphaToCoverageEnable = samples > 1;

        Background = Make(device, root, samples, vsBg, psBg, null, BlendDescription.Opaque, noDepth);
        Plant = Make(device, root, samples, vsPlant, psPlant, PlantLayout, BlendDescription.Opaque, opaqueDepth);
        FishMain = Make(device, root, samples, vsFish, psCreature, CreatureLayout, a2c, opaqueDepth);
        FishGhost = Make(device, root, samples, vsFish, psCreature, CreatureLayout, BlendDescription.NonPremultiplied, readDepth);
        RayMain = Make(device, root, samples, vsRay, psCreature, CreatureLayout, a2c, opaqueDepth);
        RayGhost = Make(device, root, samples, vsRay, psCreature, CreatureLayout, BlendDescription.NonPremultiplied, readDepth);
        CephMain = Make(device, root, samples, vsJelly, psCreature, CreatureLayout, a2c, opaqueDepth);
        CephGhost = Make(device, root, samples, vsJelly, psCreature, CreatureLayout, BlendDescription.NonPremultiplied, readDepth);
        Jelly = Make(device, root, samples, vsJelly, psJelly, CreatureLayout, BlendDescription.NonPremultiplied, readDepth);
        Particles = Make(device, root, samples, vsPart, psPart, null, BlendDescription.AlphaBlend, readDepth);
    }

    private ID3D12PipelineState Make(ID3D12Device device, ID3D12RootSignature root, int samples,
        byte[] vs, byte[] ps, InputElementDescription[]? layout, BlendDescription blend, DepthStencilDescription depth)
    {
        var desc = new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout != null ? new InputLayoutDescription(layout) : new InputLayoutDescription(),
            BlendState = blend,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = depth,
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { SceneFormat },
            DepthStencilFormat = DepthFormat,
            SampleDescription = new SampleDescription((uint)samples, 0)
        };
        var pso = device.CreateGraphicsPipelineState(desc);
        owned.Add(pso);
        return pso;
    }

    /// <summary>Final composition pass: scene texture -> swap chain back buffer.</summary>
    public static ID3D12PipelineState CreatePost(ID3D12Device device, ID3D12RootSignature root, Format backBufferFormat)
    {
        var desc = new GraphicsPipelineStateDescription
        {
            RootSignature = root,
            VertexShader = ShaderLibrary.Compile("Post.hlsl", "VSPost", "vs_5_0"),
            PixelShader = ShaderLibrary.Compile("Post.hlsl", "PSPost", "ps_5_0"),
            InputLayout = new InputLayoutDescription(),
            BlendState = BlendDescription.Opaque,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { backBufferFormat },
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0)
        };
        return device.CreateGraphicsPipelineState(desc);
    }

    public void Dispose()
    {
        foreach (var o in owned) o.Dispose();
        owned.Clear();
    }
}

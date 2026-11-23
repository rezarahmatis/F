using NEREA.Core.Utilities;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace NEREA.Rendering;

/// <summary>Raised when no usable Direct3D 12 adapter exists. The message is safe to show to the user.</summary>
public sealed class GraphicsUnavailableException : Exception
{
    public GraphicsUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Device, direct queue, fence and small resource helpers. Falls back to the WARP software adapter.</summary>
internal sealed class GpuDevice : IDisposable
{
    public IDXGIFactory4 Factory { get; private set; } = null!;
    public ID3D12Device Device { get; private set; } = null!;
    public ID3D12CommandQueue Queue { get; private set; } = null!;
    public string AdapterName { get; private set; } = "";
    public bool IsSoftware { get; private set; }

    private ID3D12Fence fence = null!;
    private ulong fenceValue;
    private AutoResetEvent fenceEvent = new(false);

    public static GpuDevice Create(bool preferLowPower)
    {
        var gpu = new GpuDevice();
        try
        {
            gpu.Factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            if (!gpu.TryHardware(preferLowPower) && !gpu.TryWarp())
                throw new GraphicsUnavailableException(
                    "NEREA could not find a graphics adapter that supports DirectX 12. The ocean is shown in simplified form; music keeps playing.");

            gpu.Queue = gpu.Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct, 0, CommandQueueFlags.None, 0));
            gpu.fence = gpu.Device.CreateFence(0, FenceFlags.None);
            Log.Info($"Graphics adapter: {gpu.AdapterName} (software: {gpu.IsSoftware})");
            return gpu;
        }
        catch (GraphicsUnavailableException)
        {
            gpu.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            gpu.Dispose();
            throw new GraphicsUnavailableException(
                "DirectX 12 could not be initialised on this PC. The ocean is shown in simplified form; music keeps playing.", ex);
        }
    }

    private bool TryHardware(bool lowPower)
    {
        var candidates = new List<IDXGIAdapter1>();
        try
        {
            using var f6 = Factory.QueryInterfaceOrNull<IDXGIFactory6>();
            if (f6 != null)
            {
                var pref = lowPower ? GpuPreference.MinimumPower : GpuPreference.HighPerformance;
                for (uint i = 0; f6.EnumAdapterByGpuPreference(i, pref, out IDXGIAdapter1? a).Success && a != null; i++)
                    candidates.Add(a);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GPU preference enumeration unavailable.", ex);
        }

        if (candidates.Count == 0)
        {
            for (uint i = 0; Factory.EnumAdapters1(i, out IDXGIAdapter1? a).Success && a != null; i++)
                candidates.Add(a);
        }

        foreach (var adapter in candidates)
        {
            var desc = adapter.Description1;
            if ((desc.Flags & AdapterFlags.Software) != 0) continue;
            try
            {
                if (D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0, out var dev).Success && dev != null)
                {
                    Device = dev;
                    AdapterName = desc.Description;
                    IsSoftware = false;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Adapter '{desc.Description}' rejected.", ex);
            }
        }
        return false;
    }

    private bool TryWarp()
    {
        try
        {
            using var warp = Factory.EnumWarpAdapter<IDXGIAdapter>();
            if (D3D12.D3D12CreateDevice<ID3D12Device>(warp, FeatureLevel.Level_11_0, out var dev).Success && dev != null)
            {
                Device = dev;
                AdapterName = "Microsoft Basic Render Driver (software)";
                IsSoftware = true;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WARP adapter unavailable.", ex);
        }
        return false;
    }

    public ID3D12Resource CreateUploadBuffer(ulong size) =>
        Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead, null);

    public ulong Signal()
    {
        fenceValue++;
        Queue.Signal(fence, fenceValue);
        return fenceValue;
    }

    public void WaitFor(ulong value)
    {
        if (value == 0 || fence.CompletedValue >= value) return;
        fence.SetEventOnCompletion(value, fenceEvent);
        fenceEvent.WaitOne();
    }

    public void WaitIdle() => WaitFor(Signal());

    public void Dispose()
    {
        try { if (Queue != null && fence != null) WaitIdle(); } catch { /* device may already be lost */ }
        fence?.Dispose();
        Queue?.Dispose();
        Device?.Dispose();
        Factory?.Dispose();
        fenceEvent.Dispose();
    }
}

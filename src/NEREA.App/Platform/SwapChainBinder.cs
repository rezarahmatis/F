using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace NEREA.App.Platform;

/// <summary>
/// Binds a DXGI composition swap chain to a XAML <see cref="SwapChainPanel"/> through
/// ISwapChainPanelNative (IID 63aad0b8-7c24-40ff-85a8-640d944cc325, the WinUI 3 interface).
/// </summary>
internal static unsafe class SwapChainBinder
{
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public static void Attach(SwapChainPanel panel, IntPtr swapChain)
    {
        IntPtr inspectable = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        try
        {
            Guid iid = IidSwapChainPanelNative;
            int hr = Marshal.QueryInterface(inspectable, in iid, out IntPtr native);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                // vtable: [0] QueryInterface, [1] AddRef, [2] Release, [3] SetSwapChain
                void** vtable = *(void***)native;
                var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
                Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }
}

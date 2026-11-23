using System.Runtime.InteropServices;
using NEREA.Core.Utilities;

namespace NEREA.App.Platform;

/// <summary>
/// Notification-area icon built directly on Shell_NotifyIcon (WinUI 3 has no tray API).
/// Handles click / context menu and balloon notifications by sub-classing the main window.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP_TRAY = 0x8001;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 1, NIIF_NOSOUND = 0x10;
    private const uint TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80, TPM_RIGHTBUTTON = 2;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800;

    public const int CmdShow = 1, CmdPlayPause = 2, CmdNext = 3, CmdPrevious = 4, CmdQuit = 5;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly IntPtr hwnd;
    private readonly SubclassProc proc;
    private IntPtr icon;
    private bool added;

    public event Action<int>? CommandInvoked;

    public TrayIcon(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        proc = WndProc; // keep the delegate alive for the native callback
        SetWindowSubclass(hwnd, proc, (UIntPtr)0x4E52, UIntPtr.Zero);
    }

    public void Show(string tooltip)
    {
        try
        {
            if (icon == IntPtr.Zero)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "NEREA.ico");
                if (File.Exists(path)) icon = LoadImage(IntPtr.Zero, path, 1, 16, 16, 0x10);
            }
            var data = NewData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            data.uCallbackMessage = WM_APP_TRAY;
            data.hIcon = icon;
            data.szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
            added = Shell_NotifyIcon(added ? NIM_MODIFY : NIM_ADD, ref data) || added;
        }
        catch (Exception ex) { Log.Warn("Tray icon could not be shown.", ex); }
    }

    public void Hide()
    {
        if (!added) return;
        try
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch { /* ignore */ }
        added = false;
    }

    public void Balloon(string title, string text)
    {
        if (!added) return;
        try
        {
            var data = NewData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title.Length > 60 ? title[..60] : title;
            data.szInfo = text.Length > 250 ? text[..250] : text;
            data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch (Exception ex) { Log.Warn("Notification failed.", ex); }
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_APP_TRAY)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK) CommandInvoked?.Invoke(CmdShow);
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdShow, "Show NEREA");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdPlayPause, "Play / Pause");
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdNext, "Next");
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdPrevious, "Previous");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)CmdQuit, "Quit");
            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            if (cmd != 0) CommandInvoked?.Invoke((int)cmd);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        Hide();
        RemoveWindowSubclass(hwnd, proc, (UIntPtr)0x4E52);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        icon = IntPtr.Zero;
    }
}

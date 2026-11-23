using System.Runtime.InteropServices;
using Microsoft.Win32;
using NEREA.Core.Utilities;

namespace NEREA.App.Platform;

/// <summary>Small Windows helpers: launch at sign-in and cursor visibility.</summary>
internal static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NEREA";

    public static void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\"");
            }
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            Log.Warn("Startup registration failed.", ex);
        }
    }

    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);

    private static bool cursorHidden;

    /// <summary>Hides / shows the mouse cursor (balanced, so repeated calls are safe).</summary>
    public static void SetCursorHidden(bool hidden)
    {
        if (hidden == cursorHidden) return;
        cursorHidden = hidden;
        try
        {
            if (hidden) { while (ShowCursor(false) >= 0) { } }
            else { while (ShowCursor(true) < 0) { } }
        }
        catch { /* cosmetic */ }
    }
}

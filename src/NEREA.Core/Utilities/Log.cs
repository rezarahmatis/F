using System.Text;
using NEREA.Core.Persistence;

namespace NEREA.Core.Utilities;

/// <summary>Tiny thread-safe file logger. Never throws.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                .Append(level).Append(' ').Append(message);
            if (ex != null) line.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                var file = Path.Combine(AppPaths.LogDirectory, "nerea.log");
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                {
                    var old = Path.Combine(AppPaths.LogDirectory, "nerea.old.log");
                    File.Copy(file, old, true);
                    File.Delete(file);
                }
                File.AppendAllText(file, line.AppendLine().ToString());
            }
            System.Diagnostics.Debug.WriteLine(line.ToString());
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}

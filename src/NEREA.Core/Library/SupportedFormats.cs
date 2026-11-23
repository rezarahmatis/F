namespace NEREA.Core.Library;

/// <summary>Audio containers NEREA accepts. Decoding is done by Media Foundation / NVorbis in the audio engine.</summary>
public static class SupportedFormats
{
    public static readonly string[] Extensions =
    {
        ".mp3", ".wav", ".flac", ".ogg", ".oga", ".m4a", ".aac", ".mp4", ".wma", ".aif", ".aiff", ".alac"
    };

    private static readonly HashSet<string> Set = new(Extensions, StringComparer.OrdinalIgnoreCase);

    public static bool IsSupported(string path) => Set.Contains(Path.GetExtension(path));
}

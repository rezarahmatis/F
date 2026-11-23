namespace NEREA.Core.Persistence;

/// <summary>All on-disk locations used by NEREA. Everything lives under %LOCALAPPDATA%\NEREA.</summary>
public static class AppPaths
{
    private static string? overrideRoot;

    /// <summary>Redirects storage (used by tests).</summary>
    public static void UseRoot(string path) => overrideRoot = path;

    public static string Root => overrideRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEREA");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string AquariumsFile => Path.Combine(Root, "aquariums.json");
    public static string LibraryDatabaseFile => Path.Combine(Root, "library.db");
    public static string ArtDirectory => Path.Combine(Root, "Art");
    public static string LogDirectory => Path.Combine(Root, "Logs");
    public static string UserContentDirectory => Path.Combine(Root, "Content");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ArtDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(UserContentDirectory);
    }
}

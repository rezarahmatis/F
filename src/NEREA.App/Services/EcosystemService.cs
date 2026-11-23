using NEREA.Core.Content;
using NEREA.Core.Models;
using NEREA.Rendering;

namespace NEREA.App.Services;

/// <summary>
/// The single authority on "which world, which animals, which mood". It pushes changes to the render host
/// (when GPU rendering is available) and persists them per world.
/// </summary>
public sealed class EcosystemService : IDisposable
{
    private readonly ContentCatalog catalog;
    private readonly SettingsService settings;
    private readonly Timer applyTimer;

    public EcosystemService(ContentCatalog catalog, SettingsService settings)
    {
        this.catalog = catalog;
        this.settings = settings;
        World = catalog.DefaultWorld;
        Config = new EcosystemConfig();
        applyTimer = new Timer(_ => Host?.SetEcosystem(Config.Clone()), null, Timeout.Infinite, Timeout.Infinite);
    }

    public RenderHost? Host { get; set; }
    public WorldDefinition World { get; private set; }
    public EcosystemConfig Config { get; private set; }
    public AtmosphereMood Mood { get; private set; }

    public event Action<WorldDefinition>? WorldChanged;
    public event Action? ConfigChanged;
    public event Action<AtmosphereMood>? MoodChanged;

    public ContentCatalog Catalog => catalog;

    public void Initialize()
    {
        LoadWorld(settings.Current.WorldId, resetMood: false);
        Mood = settings.Current.Mood;
        Host?.SetMood(Mood);
    }

    public void LoadWorld(string id, bool resetMood = true)
    {
        World = catalog.FindWorld(id) ?? catalog.DefaultWorld;
        Config = settings.Current.Ecosystems.TryGetValue(World.Id, out var saved) && saved.Species.Count > 0
            ? saved.Clone()
            : catalog.DefaultEcosystem(World);
        settings.Current.Ecosystems[World.Id] = Config;
        settings.Current.WorldId = World.Id;
        if (resetMood) Mood = World.Mood;
        settings.Current.Mood = Mood;
        settings.Current.ActiveAquariumId = null;

        Host?.SetWorld(World, Config);
        Host?.SetMood(Mood);
        settings.SaveSoon();
        WorldChanged?.Invoke(World);
        MoodChanged?.Invoke(Mood);
    }

    public int GetCount(string speciesId) => Config.Species.FirstOrDefault(s => s.SpeciesId == speciesId)?.Count ?? 0;
    public bool IsEnabled(string speciesId) => Config.Species.FirstOrDefault(s => s.SpeciesId == speciesId)?.Enabled ?? true;

    public void SetCount(string speciesId, int count)
    {
        count = Math.Clamp(count, 0, 40);
        var entry = Config.Species.FirstOrDefault(s => s.SpeciesId == speciesId);
        if (entry == null) Config.Species.Add(new SpeciesPopulation(speciesId, count));
        else entry.Count = count;
        Commit();
    }

    public void SetEnabled(string speciesId, bool enabled)
    {
        var entry = Config.Species.FirstOrDefault(s => s.SpeciesId == speciesId);
        if (entry == null) Config.Species.Add(new SpeciesPopulation(speciesId, 0, enabled));
        else entry.Enabled = enabled;
        Commit();
    }

    public void ResetToDefault()
    {
        Config = catalog.DefaultEcosystem(World);
        settings.Current.Ecosystems[World.Id] = Config;
        Commit();
    }

    public void SetMood(AtmosphereMood mood)
    {
        Mood = mood;
        settings.Current.Mood = mood;
        Host?.SetMood(mood);
        settings.SaveSoon();
        MoodChanged?.Invoke(mood);
    }

    /// <summary>Applies a saved aquarium: world, population and atmosphere.</summary>
    public void ApplyAquarium(SavedAquarium aquarium)
    {
        World = catalog.FindWorld(aquarium.WorldId) ?? catalog.DefaultWorld;
        Config = new EcosystemConfig { Species = aquarium.Species.Select(s => s.Clone()).ToList() };
        Mood = aquarium.Mood;
        settings.Current.Ecosystems[World.Id] = Config;
        settings.Current.WorldId = World.Id;
        settings.Current.Mood = Mood;
        settings.Current.ActiveAquariumId = aquarium.Id;

        Host?.SetWorld(World, Config);
        Host?.SetMood(Mood);
        settings.SaveSoon();
        WorldChanged?.Invoke(World);
        MoodChanged?.Invoke(Mood);
    }

    public SavedAquarium Capture(string name, long? playlistId, string? playlistName) => new()
    {
        Name = name.Trim(),
        WorldId = World.Id,
        Mood = Mood,
        Species = Config.Species.Where(s => s.Count > 0).Select(s => s.Clone()).ToList(),
        PlaylistId = playlistId,
        PlaylistName = playlistName
    };

    private void Commit()
    {
        settings.SaveSoon();
        ConfigChanged?.Invoke();
        // Coalesce rapid +/- clicks into one population rebuild
        applyTimer.Change(700, Timeout.Infinite);
    }

    public void Dispose() => applyTimer.Dispose();
}

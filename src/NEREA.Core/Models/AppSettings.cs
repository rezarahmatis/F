namespace NEREA.Core.Models;

/// <summary>How many of one species live in an ecosystem.</summary>
public sealed class SpeciesPopulation
{
    public string SpeciesId { get; set; } = "";
    public int Count { get; set; }
    public bool Enabled { get; set; } = true;

    public SpeciesPopulation() { }
    public SpeciesPopulation(string id, int count, bool enabled = true)
    {
        SpeciesId = id;
        Count = count;
        Enabled = enabled;
    }

    public SpeciesPopulation Clone() => new(SpeciesId, Count, Enabled);
}

/// <summary>The species configuration of one world.</summary>
public sealed class EcosystemConfig
{
    public List<SpeciesPopulation> Species { get; set; } = new();

    public EcosystemConfig Clone() => new() { Species = Species.Select(s => s.Clone()).ToList() };

    public int TotalEnabled => Species.Where(s => s.Enabled).Sum(s => s.Count);
}

/// <summary>A user-created aquarium: a world, a population, an atmosphere and optionally a playlist.</summary>
public sealed class SavedAquarium
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My Aquarium";
    public string WorldId { get; set; } = "moonlit-pond";
    public AtmosphereMood Mood { get; set; } = AtmosphereMood.Calm;
    public List<SpeciesPopulation> Species { get; set; } = new();
    public long? PlaylistId { get; set; }
    public string? PlaylistName { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    public SavedAquarium Duplicate(string newName) => new()
    {
        Name = newName,
        WorldId = WorldId,
        Mood = Mood,
        Species = Species.Select(s => s.Clone()).ToList(),
        PlaylistId = PlaylistId,
        PlaylistName = PlaylistName
    };
}

public sealed class SavedAquariumFile
{
    public List<SavedAquarium> Aquariums { get; set; } = new();
}

/// <summary>Persisted user settings and lightweight state. Stored as JSON in the local app data folder.</summary>
public sealed class AppSettings
{
    /// <summary>Shallow copy (collections are shared) - used to hand a stable snapshot to the render thread.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    // Audio
    public string? OutputDeviceId { get; set; }
    public double Volume { get; set; } = 0.7;
    public bool Muted { get; set; }
    public bool NormalizeVolume { get; set; } = true;
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.All;

    // Visual
    public PerformanceMode Performance { get; set; } = PerformanceMode.Balanced;
    public int FpsLimit { get; set; } = 60;
    public double ParticleDensity { get; set; } = 1.0;
    public bool Reflections { get; set; } = true;
    public bool Shadows { get; set; } = true;
    /// <summary>0 = low, 1 = medium, 2 = high.</summary>
    public int WaterQuality { get; set; } = 2;
    public double FishPopulation { get; set; } = 1.0;

    // Interaction
    public double MouseSensitivity { get; set; } = 1.0;
    public double RippleStrength { get; set; } = 1.0;
    public double FishReaction { get; set; } = 1.0;

    // Performance
    public bool BatteryOptimization { get; set; } = true;
    public bool PauseWhenMinimized { get; set; } = true;

    // Application
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; }
    public ThemePreference Theme { get; set; } = ThemePreference.Dark;
    public bool Fullscreen { get; set; }
    public double UiScale { get; set; } = 1.0;
    public bool ReducedMotion { get; set; }
    public bool ShowNotifications { get; set; } = true;

    // World state
    public string WorldId { get; set; } = "moonlit-pond";
    public AtmosphereMood Mood { get; set; } = AtmosphereMood.Calm;
    public string? ActiveAquariumId { get; set; }
    /// <summary>Per-world edited species configuration, keyed by world id.</summary>
    public Dictionary<string, EcosystemConfig> Ecosystems { get; set; } = new();

    // Playback state
    public List<long> QueueTrackIds { get; set; } = new();
    public int QueueIndex { get; set; } = -1;
    public double PositionSeconds { get; set; }

    // Window
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 860;
}

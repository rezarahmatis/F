using System.Numerics;
using System.Text.Json.Serialization;
using NEREA.Core.Models;

namespace NEREA.Core.Content;

/// <summary>One layer of vegetation / structures scattered over the floor of a world.</summary>
public sealed class PlantLayer
{
    public PlantKind Kind { get; set; } = PlantKind.Blade;
    public int Count { get; set; } = 30;
    public float Height { get; set; } = 1.5f;
    public float Width { get; set; } = 0.1f;
    [JsonPropertyName("a")] public string ColorA { get; set; } = "#2f6b3a";
    [JsonPropertyName("b")] public string ColorB { get; set; } = "#8bc06a";
    public float ZMin { get; set; } = -12f;
    public float ZMax { get; set; } = -3f;
    /// <summary>Emissive strength (crystals, glowing kelp).</summary>
    public float Glow { get; set; }
}

/// <summary>
/// Data-driven world: palette, light, water behaviour, flora and default ecosystem.
/// New worlds are added in Assets/Worlds/worlds.json - no code changes required.
/// </summary>
public sealed class WorldDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public WorldCategory Category { get; set; }

    // Palette
    public string Surface { get; set; } = "#6fb8d8";
    public string Deep { get; set; } = "#0a3050";
    public string? Fog { get; set; }
    public string Light { get; set; } = "#fff4dc";
    public string Sand { get; set; } = "#9a8e72";
    public string Rock { get; set; } = "#3c4448";

    // Light & water
    public float LightIntensity { get; set; } = 1.0f;
    public float Rays { get; set; } = 0.6f;
    public float Caustics { get; set; } = 0.7f;
    /// <summary>Fog density (higher = murkier).</summary>
    public float Turbidity { get; set; } = 0.06f;
    /// <summary>0 = shallow and bright, 1 = deep and dark.</summary>
    public float Depth { get; set; } = 0.3f;
    /// <summary>Strength of ambient current (vegetation sway, particle drift).</summary>
    public float Current { get; set; } = 0.4f;
    public float SurfaceY { get; set; } = 4.5f;
    public float FloorY { get; set; } = -4.2f;
    /// <summary>Floor descends with distance when positive (drop-offs, trenches).</summary>
    public float FloorSlope { get; set; }

    // Particles
    public float Particles { get; set; } = 0.5f;
    public float ParticleGlow { get; set; }
    public string ParticleColor { get; set; } = "#cfe8f0";
    public float Bubbles { get; set; } = 0.3f;

    // Enclosure
    /// <summary>0 = open water, 1 = narrow cave with rock walls.</summary>
    public float Enclosure { get; set; }
    public CeilingKind Ceiling { get; set; } = CeilingKind.Surface;

    // Grade
    public float Exposure { get; set; } = 1.0f;
    public float Saturation { get; set; } = 1.0f;
    public float Vignette { get; set; } = 0.35f;

    public List<PlantLayer> Plants { get; set; } = new();
    /// <summary>Default ecosystem: species id -> count.</summary>
    public Dictionary<string, int> Fish { get; set; } = new();
    public AtmosphereMood Mood { get; set; } = AtmosphereMood.Balanced;
    /// <summary>Optional explicit list of species categories available in this world.</summary>
    public List<SpeciesCategory>? Allow { get; set; }

    public Vector3 SurfaceColor => ColorUtil.Parse(Surface);
    public Vector3 DeepColor => ColorUtil.Parse(Deep);
    public Vector3 FogColor => ColorUtil.Parse(Fog ?? Deep);
    public Vector3 LightColor => ColorUtil.Parse(Light);
    public Vector3 SandColor => ColorUtil.Parse(Sand);
    public Vector3 RockColor => ColorUtil.Parse(Rock);
    public Vector3 ParticleTint => ColorUtil.Parse(ParticleColor);

    public string CategoryLabel => Category switch
    {
        WorldCategory.Freshwater => "Freshwater",
        WorldCategory.Ocean => "Ocean",
        _ => "Exotic"
    };

    /// <summary>Species categories that make sense in this world.</summary>
    public IReadOnlyList<SpeciesCategory> AllowedCategories
    {
        get
        {
            if (Allow is { Count: > 0 }) return Allow;
            return Category switch
            {
                WorldCategory.Freshwater => new[] { SpeciesCategory.Freshwater, SpeciesCategory.AquaticAnimals },
                WorldCategory.Ocean => Depth >= 0.5f
                    ? new[] { SpeciesCategory.TropicalReef, SpeciesCategory.LargeOcean, SpeciesCategory.DeepSea, SpeciesCategory.AquaticAnimals }
                    : new[] { SpeciesCategory.TropicalReef, SpeciesCategory.LargeOcean, SpeciesCategory.AquaticAnimals },
                _ => new[]
                {
                    SpeciesCategory.Freshwater, SpeciesCategory.TropicalReef, SpeciesCategory.LargeOcean,
                    SpeciesCategory.DeepSea, SpeciesCategory.AquaticAnimals
                }
            };
        }
    }

    /// <summary>Floor height at depth <paramref name="z"/> (shared by the simulation and the shaders).</summary>
    public float FloorHeightAt(float z) => FloorY + FloorSlope * z;

    public bool IsValid(out string reason)
    {
        if (string.IsNullOrWhiteSpace(Id)) { reason = "missing id"; return false; }
        if (string.IsNullOrWhiteSpace(Name)) { reason = "missing name"; return false; }
        reason = "";
        return true;
    }
}

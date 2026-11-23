using System.Numerics;
using NEREA.Core.Models;

namespace NEREA.Core.Content;

/// <summary>
/// Data-driven description of one species: silhouette parameters (consumed by the procedural
/// body shader), colours, pattern and behavioural traits (consumed by the simulation).
/// New species are added in Assets/Fish/species.json - no code changes required.
/// </summary>
public sealed class SpeciesDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public SpeciesCategory Category { get; set; }
    public BodyPlan Plan { get; set; } = BodyPlan.Fish;

    // ---- Silhouette (relative to Length unless noted) ----
    /// <summary>Body length in world units (Ray: body length; Jelly: bell diameter).</summary>
    public float Length { get; set; } = 0.8f;
    /// <summary>Body height. Ray: thickness.</summary>
    public float Height { get; set; } = 0.22f;
    /// <summary>Body width. Ray: wing span / length.</summary>
    public float Width { get; set; } = 0.12f;
    /// <summary>Tail fin length. Ray: tail length. Jelly/Cephalopod: tentacle length.</summary>
    public float Tail { get; set; } = 0.2f;
    public float Dorsal { get; set; } = 0.14f;
    /// <summary>Position of the thickest part of the body (0 = nose, 1 = tail).</summary>
    public float Peak { get; set; } = 0.38f;
    /// <summary>Tail fork depth (0 = rounded, 1 = deeply forked).</summary>
    public float Fork { get; set; } = 0.3f;
    /// <summary>0 = blunt head, 1 = long pointed snout / bill.</summary>
    public float Snout { get; set; }
    /// <summary>Lateral head extension (hammerhead).</summary>
    public float Hammer { get; set; }
    /// <summary>Body curvature (seahorse, shrimp).</summary>
    public float Curl { get; set; }
    /// <summary>Leg presence for crustaceans (Ray plan).</summary>
    public float Legs { get; set; }
    /// <summary>Wing / bell pulse amplitude for Ray and Jelly plans.</summary>
    public float Flap { get; set; } = 0.4f;

    // ---- Behaviour ----
    /// <summary>Cruising speed in body lengths per second.</summary>
    public float Speed { get; set; } = 1.0f;
    /// <summary>0 = solitary, 1 = tight school.</summary>
    public float Schooling { get; set; }
    public int GroupSize { get; set; } = 1;
    /// <summary>Preferred depth band, 0 = surface, 1 = floor.</summary>
    public float DepthMin { get; set; } = 0.15f;
    public float DepthMax { get; set; } = 0.85f;
    public float Curiosity { get; set; } = 0.3f;
    public float Timidity { get; set; } = 0.4f;
    public float Restfulness { get; set; } = 0.2f;
    /// <summary>Radius of the home territory; 0 = free ranging.</summary>
    public float Territory { get; set; }
    /// <summary>0 = open water, 1 = hugs the floor.</summary>
    public float Bottom { get; set; }

    // ---- Appearance ----
    public string Primary { get; set; } = "#8a9aa8";
    public string Secondary { get; set; } = "#d8e0e6";
    public string Accent { get; set; } = "#c0a060";
    public PatternKind Pattern { get; set; } = PatternKind.Countershade;
    public float PatternScale { get; set; } = 3f;
    /// <summary>Self illumination (bioluminescence).</summary>
    public float Glow { get; set; }

    public Vector3 PrimaryColor => ColorUtil.Parse(Primary);
    public Vector3 SecondaryColor => ColorUtil.Parse(Secondary);
    public Vector3 AccentColor => ColorUtil.Parse(Accent);

    public string CategoryLabel => Category switch
    {
        SpeciesCategory.Freshwater => "Freshwater",
        SpeciesCategory.TropicalReef => "Tropical reef",
        SpeciesCategory.LargeOcean => "Large ocean",
        SpeciesCategory.DeepSea => "Deep sea",
        _ => "Aquatic animals"
    };

    public bool IsValid(out string reason)
    {
        if (string.IsNullOrWhiteSpace(Id)) { reason = "missing id"; return false; }
        if (string.IsNullOrWhiteSpace(Name)) { reason = "missing name"; return false; }
        if (Length <= 0.02f || Length > 20f) { reason = "length out of range"; return false; }
        reason = "";
        return true;
    }
}

namespace NEREA.Core.Models;

/// <summary>Overall quality / power preset.</summary>
public enum PerformanceMode { Ultra, High, Balanced, Performance, Battery }

public enum RepeatMode { Off, All, One }

/// <summary>Overall temperament of an ecosystem (activity, light, motion).</summary>
public enum AtmosphereMood { Calm, Balanced, Lively, Nocturnal }

public enum ThemePreference { System, Dark, Light }

/// <summary>How much interface is shown on top of the world.</summary>
public enum PresentationMode { Normal, Ambient, Cinematic }

public enum SpeciesCategory { Freshwater, TropicalReef, LargeOcean, DeepSea, AquaticAnimals }

/// <summary>Which procedural body the renderer uses for a species. Adding species never needs a new renderer.</summary>
public enum BodyPlan
{
    /// <summary>Lofted fusiform body with fins and tail (fish, sharks, eels, shrimp, seahorse).</summary>
    Fish,
    /// <summary>Flat winged body (rays, sea turtle, crab).</summary>
    Ray,
    /// <summary>Pulsing bell with trailing tentacles (jellyfish).</summary>
    Jelly,
    /// <summary>Mantle plus arms, oriented along the swim direction (squid, cuttlefish, octopus).</summary>
    Cephalopod
}

public enum PatternKind
{
    Plain = 0,
    Bands = 1,
    Stripe = 2,
    Patches = 3,
    Spots = 4,
    Countershade = 5,
    Photophores = 6,
    Lines = 7
}

public enum WorldCategory { Freshwater, Ocean, Exotic }

public enum PlantKind { Blade = 0, Kelp = 1, Reed = 2, Coral = 3, Crystal = 4, Pillar = 5 }

public enum CeilingKind { Surface = 0, Rock = 1, Ice = 2 }

using NEREA.Core.Models;

namespace NEREA.Core.Engine.Quality;

/// <summary>Resolved rendering budget. Computed from settings, power state, presentation mode and adaptive scaling.</summary>
public readonly record struct RenderQuality(
    float RenderScale,
    bool Msaa,
    int MaxFps,
    float ParticleScale,
    bool Shadows,
    bool Reflections,
    int WaterQuality,
    float FishScale,
    bool Bloom)
{
    public static RenderQuality Resolve(AppSettings s, PresentationMode mode, bool onBatterySaver, float adaptive = 1f)
    {
        float scale; bool msaa; int fps; float particles; bool shadows; bool refl; int water; float fish; bool bloom;

        switch (s.Performance)
        {
            case PerformanceMode.Ultra:
                scale = 1.0f; msaa = true; fps = Math.Max(30, s.FpsLimit); particles = 1.4f; shadows = true; refl = true; water = 2; fish = 1.0f; bloom = true; break;
            case PerformanceMode.High:
                scale = 1.0f; msaa = true; fps = Math.Max(30, s.FpsLimit); particles = 1.1f; shadows = true; refl = true; water = 2; fish = 1.0f; bloom = true; break;
            case PerformanceMode.Performance:
                scale = 0.75f; msaa = false; fps = Math.Min(60, Math.Max(30, s.FpsLimit)); particles = 0.6f; shadows = false; refl = false; water = 1; fish = 0.8f; bloom = false; break;
            case PerformanceMode.Battery:
                scale = 0.6f; msaa = false; fps = 30; particles = 0.4f; shadows = false; refl = false; water = 0; fish = 0.6f; bloom = false; break;
            default: // Balanced
                scale = 1.0f; msaa = true; fps = Math.Min(60, Math.Max(30, s.FpsLimit)); particles = 0.85f; shadows = true; refl = true; water = 1; fish = 1.0f; bloom = true; break;
        }

        // User overrides stack on top of the preset (only ever reduce, never exceed the preset's budget)
        particles *= (float)Math.Clamp(s.ParticleDensity, 0.0, 2.0);
        shadows &= s.Shadows;
        refl &= s.Reflections;
        water = Math.Min(water, s.WaterQuality);
        fish *= (float)Math.Clamp(s.FishPopulation, 0.25, 1.5);

        if (s.BatteryOptimization && onBatterySaver)
        {
            scale = Math.Min(scale, 0.7f); msaa = false; fps = Math.Min(fps, 30);
            particles *= 0.6f; shadows = false; refl = false; water = Math.Min(water, 1); bloom = false;
        }

        // Ambient mode: stable, quiet, low power for long sessions
        if (mode == PresentationMode.Ambient)
        {
            scale = Math.Min(scale, 0.75f); msaa = false; fps = Math.Min(fps, 30);
            particles *= 0.6f; shadows = false; water = Math.Min(water, 1);
        }

        // Adaptive scaling reacts to measured frame times
        adaptive = Math.Clamp(adaptive, 0.4f, 1f);
        if (adaptive < 1f)
        {
            scale = Math.Max(0.5f, scale * (0.6f + 0.4f * adaptive));
            particles *= adaptive;
            if (adaptive < 0.8f) { shadows = false; msaa = false; }
            if (adaptive < 0.6f) { refl = false; bloom = false; water = Math.Min(water, 0); }
            fish *= Math.Max(0.5f, adaptive);
        }

        return new RenderQuality(Math.Clamp(scale, 0.4f, 1.5f), msaa, fps, Math.Max(0f, particles), shadows, refl, water, fish, bloom);
    }
}

/// <summary>
/// Watches frame times and lowers / restores a 0.4..1 quality factor so the experience keeps a stable frame pace
/// on weaker GPUs without any user interaction.
/// </summary>
public sealed class AdaptiveQualityController
{
    private const float WindowSeconds = 2f;
    private float accumulated;
    private int frames;
    private int slowWindows;
    private int fastWindows;

    public float Factor { get; private set; } = 1f;

    /// <summary>Reports a frame time. Returns true when <see cref="Factor"/> changed.</summary>
    public bool Observe(float frameSeconds, int targetFps)
    {
        accumulated += frameSeconds;
        frames++;
        if (accumulated < WindowSeconds) return false;

        float avg = accumulated / frames;
        float budget = 1f / Math.Max(20, targetFps);
        accumulated = 0; frames = 0;

        bool changed = false;
        if (avg > budget * 1.25f)
        {
            fastWindows = 0;
            if (++slowWindows >= 2 && Factor > 0.4f) { Factor = MathF.Max(0.4f, Factor - 0.15f); slowWindows = 0; changed = true; }
        }
        else if (avg < budget * 0.8f)
        {
            slowWindows = 0;
            if (++fastWindows >= 5 && Factor < 1f) { Factor = MathF.Min(1f, Factor + 0.1f); fastWindows = 0; changed = true; }
        }
        else { slowWindows = 0; fastWindows = 0; }
        return changed;
    }

    public void Reset()
    {
        Factor = 1f; accumulated = 0; frames = 0; slowWindows = 0; fastWindows = 0;
    }
}

using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Models;

namespace NEREA.Core.Engine.Quality;

/// <summary>Maps a chosen atmosphere and the current music into overall ecosystem activity and light.</summary>
public static class MoodProfile
{
    public static float BaseActivity(AtmosphereMood mood) => mood switch
    {
        AtmosphereMood.Calm => 0.75f,
        AtmosphereMood.Lively => 1.3f,
        AtmosphereMood.Nocturnal => 0.8f,
        _ => 1.0f
    };

    public static float LightScale(AtmosphereMood mood) => mood switch
    {
        AtmosphereMood.Calm => 0.92f,
        AtmosphereMood.Lively => 1.1f,
        AtmosphereMood.Nocturnal => 0.62f,
        _ => 1.0f
    };

    /// <summary>
    /// Calm piano -> slower fish. Driving electronic -> livelier. Without music the world idles gently.
    /// Always bounded so the ecosystem stays organic.
    /// </summary>
    public static float Activity(AtmosphereMood mood, in MusicState music)
    {
        float baseActivity = BaseActivity(mood);
        if (!music.IsPlaying) return baseActivity * 0.9f;
        float drive = 1f - music.Calm;
        float factor = 0.82f + 0.45f * drive + 0.15f * music.Energy;
        return Math.Clamp(baseActivity * factor, 0.5f, 1.6f);
    }
}

namespace NEREA.Core.Engine.AudioAnalysis;

/// <summary>
/// Smoothed musical descriptors consumed by the ecosystem. All values are 0..1 and already
/// interpolated - they never jump, so the world responds organically instead of flashing.
/// </summary>
public readonly record struct MusicState(
    float Bass,
    float Mid,
    float Treble,
    float Energy,
    float Beat,
    float Activity,
    float Calm,
    float Brightness,
    bool IsPlaying)
{
    /// <summary>The state of a world with no music: everything at rest, calm.</summary>
    public static MusicState Silent => new(0, 0, 0, 0, 0, 0, 1f, 0.5f, false);
}

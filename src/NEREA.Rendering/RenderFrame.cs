using System.Numerics;
using NEREA.Core.Content;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Engine.FishSimulation;
using NEREA.Core.Engine.Quality;

namespace NEREA.Rendering;

/// <summary>Everything the renderer needs for one frame. Reused every frame (no allocations).</summary>
public sealed class RenderFrame
{
    public const int MaxRipples = 16;

    public float Time;
    public SceneCamera Camera = null!;
    public WorldDefinition World = null!;
    public MusicState Music;
    public RenderQuality Quality;
    /// <summary>World transition fade, 0 (black) .. 1 (visible).</summary>
    public float Fade = 1f;
    /// <summary>Mood based light multiplier.</summary>
    public float LightScale = 1f;
    public bool ReducedMotion;
    public int RippleCount;
    /// <summary>x,y = screen uv, z = start time, w = strength.</summary>
    public readonly Vector4[] Ripples = new Vector4[MaxRipples];
}

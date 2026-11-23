using System.Numerics;
using System.Runtime.InteropServices;

namespace NEREA.Rendering;

/// <summary>Mirrors <c>cbuffer Frame</c> in Common.hlsli. The 16 ripple float4s follow this struct in memory.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    public Vector4 CamPos;        // xyz, time
    public Vector4 Screen;        // width, height, projX, projY
    public Vector4 Music;         // bass, mid, treble, energy
    public Vector4 Music2;        // beat, calm, brightness, activity
    public Vector4 SurfaceCol;    // rgb, surfaceY
    public Vector4 DeepCol;       // rgb, floorY
    public Vector4 FogCol;        // rgb, turbidity
    public Vector4 LightCol;      // rgb, intensity
    public Vector4 SandCol;       // rgb, floor slope
    public Vector4 RockCol;       // rgb, enclosure
    public Vector4 Water;         // rays, caustics, depth, current
    public Vector4 Grade;         // exposure, saturation, vignette, ceiling kind
    public Vector4 ParticleCol;   // rgb, glow
    public Vector4 Quality;       // water quality, particle scale, motion scale, bloom
    public Vector4 Misc;          // ripple count, reflections, shadows, bubbles
    public Vector4 Extra;         // fade, wall distance
}

/// <summary>Per-animal instance data (64 bytes). Layout matches the IPOS/IFWD/IUP/IMISC vertex inputs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CreatureInstance
{
    public Vector4 PosLength;     // xyz, body length
    public Vector4 ForwardPhase;  // xyz, animation phase
    public Vector4 UpSwim;        // xyz, swim effort
    public Vector4 Misc;          // species index, bend, seed, glow phase
}

/// <summary>Per-plant instance data (64 bytes). Layout matches IPOS/IPAR/ICOLA/ICOLB.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PlantInstance
{
    public Vector4 PosHeight;     // base xyz, height
    public Vector4 Params;        // width, seed, kind, glow
    public Vector4 ColorA;        // rgb base colour, variation seed
    public Vector4 ColorB;        // rgb tip colour
}

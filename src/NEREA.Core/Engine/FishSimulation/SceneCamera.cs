using System.Numerics;

namespace NEREA.Core.Engine.FishSimulation;

/// <summary>
/// The single camera shared by simulation (visible bounds, pointer rays) and renderer (matrices).
/// Right-handed, looking down -Z, depth range 0..1 (Direct3D).
/// </summary>
public sealed class SceneCamera
{
    public static readonly Vector3 BasePosition = new(0f, 0.3f, 9f);
    public static readonly Vector3 BaseTarget = new(0f, -0.35f, -6f);

    public float FovY { get; } = 52f * MathF.PI / 180f;
    public float Aspect { get; private set; } = 16f / 9f;
    public float Near { get; } = 0.2f;
    public float Far { get; } = 90f;

    public Vector3 Position { get; private set; } = BasePosition;
    public Vector3 Target { get; private set; } = BaseTarget;
    public Matrix4x4 View { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 Projection { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 ViewProjection { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 InverseViewProjection { get; private set; } = Matrix4x4.Identity;

    public SceneCamera() => Update(0f, Vector2.Zero, 1f);

    public void SetAspect(float aspect)
    {
        Aspect = Math.Clamp(aspect, 0.5f, 4f);
        RebuildMatrices();
    }

    /// <summary>Slow cinematic drift plus a gentle parallax toward the pointer.</summary>
    /// <param name="motionScale">0 disables motion (reduced-motion setting).</param>
    public void Update(float time, Vector2 pointerNdc, float motionScale)
    {
        var drift = new Vector3(MathF.Sin(time * 0.071f) * 0.55f, MathF.Sin(time * 0.053f) * 0.18f, MathF.Sin(time * 0.037f) * 0.4f);
        var parallax = new Vector3(pointerNdc.X * 0.45f, pointerNdc.Y * 0.22f, 0f);
        Position = BasePosition + (drift + parallax) * motionScale;
        Target = BaseTarget + (new Vector3(pointerNdc.X * 0.9f, pointerNdc.Y * 0.35f, 0f) + drift * 0.4f) * motionScale;
        RebuildMatrices();
    }

    private void RebuildMatrices()
    {
        View = Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(FovY, Aspect, Near, Far);
        ViewProjection = View * Projection;
        if (!Matrix4x4.Invert(ViewProjection, out var inv)) inv = Matrix4x4.Identity;
        InverseViewProjection = inv;
    }

    /// <summary>World-space ray through a normalised device coordinate (-1..1, y up).</summary>
    public (Vector3 Origin, Vector3 Direction) Ray(Vector2 ndc)
    {
        var near = Unproject(new Vector3(ndc, 0f));
        var far = Unproject(new Vector3(ndc, 1f));
        return (near, Vector3.Normalize(far - near));
    }

    private Vector3 Unproject(Vector3 ndc)
    {
        var p = Vector4.Transform(new Vector4(ndc, 1f), InverseViewProjection);
        return new Vector3(p.X, p.Y, p.Z) / p.W;
    }

    /// <summary>Half extents of the visible slice of the world at depth <paramref name="z"/>.</summary>
    public (float HalfWidth, float HalfHeight) VisibleHalfExtentsAt(float z)
    {
        float dist = MathF.Max(0.5f, BasePosition.Z - z);
        float halfH = MathF.Tan(FovY * 0.5f) * dist;
        return (halfH * Aspect, halfH);
    }
}

using System.Numerics;
using NEREA.Core.Content;
using NEREA.Core.Models;

namespace NEREA.Rendering;

/// <summary>Scatters a world's vegetation layers across its floor (deterministic per world).</summary>
internal static class PlantScatter
{
    public const int MaxPlants = 1800;

    public static float WallDistance(WorldDefinition world) =>
        world.Enclosure > 0.01f ? 16f + (4.2f - 16f) * world.Enclosure + 1.2f : 0f;

    public static PlantInstance[] Build(WorldDefinition world, float densityScale)
    {
        var list = new List<PlantInstance>(512);
        var rng = new Random(StableHash(world.Id));
        float wall = WallDistance(world);
        float tanHalf = MathF.Tan(26f * MathF.PI / 180f);

        foreach (var layer in world.Plants)
        {
            int count = (int)MathF.Round(layer.Count * Math.Clamp(densityScale, 0.2f, 1.5f));
            var colA = ColorUtil.Parse(layer.ColorA);
            var colB = ColorUtil.Parse(layer.ColorB);
            bool cluster = layer.Kind == PlantKind.Coral;

            for (int i = 0; i < count && list.Count < MaxPlants; i++)
            {
                float z = Lerp(layer.ZMin, layer.ZMax, (float)rng.NextDouble());
                float hw = (SceneCameraZ - z) * tanHalf * 2.3f;
                if (wall > 0f) hw = MathF.Min(hw, wall - 0.6f);
                float x = Lerp(-hw, hw, (float)rng.NextDouble());
                int pieces = cluster ? 3 + rng.Next(3) : 1;

                for (int k = 0; k < pieces && list.Count < MaxPlants; k++)
                {
                    float px = x + (cluster ? ((float)rng.NextDouble() - 0.5f) * layer.Width * 1.6f : 0f);
                    float pz = z + (cluster ? ((float)rng.NextDouble() - 0.5f) * layer.Width * 1.6f : 0f);
                    float floor = world.FloorHeightAt(pz);
                    float height = layer.Height * Lerp(0.6f, 1.4f, (float)rng.NextDouble());
                    if (layer.Kind == PlantKind.Pillar) height *= Lerp(0.45f, 1.0f, (float)rng.NextDouble());
                    float mix = (float)rng.NextDouble();
                    list.Add(new PlantInstance
                    {
                        PosHeight = new Vector4(px, floor - 0.04f, pz, height),
                        Params = new Vector4(layer.Width * Lerp(0.8f, 1.25f, (float)rng.NextDouble()),
                                             (float)rng.NextDouble(), (float)layer.Kind, layer.Glow),
                        ColorA = new Vector4(Vector3.Lerp(colA, colB, mix * 0.25f), (float)rng.NextDouble()),
                        ColorB = new Vector4(Vector3.Lerp(colB, colA, mix * 0.2f), 1f)
                    });
                }
            }
        }
        return list.ToArray();
    }

    private const float SceneCameraZ = 9f;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }
}

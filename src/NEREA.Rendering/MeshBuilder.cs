using System.Numerics;

namespace NEREA.Rendering;

internal sealed class MeshData
{
    public readonly List<Vector4> Vertices = new();
    public readonly List<uint> Indices = new();

    /// <summary>
    /// Adds a (nA+1) x (nB+1) grid. Each vertex stores (a, b, kind, aux); the vertex shaders turn these
    /// parameters into positions, so the mesh itself is species independent.
    /// </summary>
    public void Grid(float kind, float aux, int nA, int nB, float a0, float a1, float b0, float b1)
    {
        uint start = (uint)Vertices.Count;
        for (int i = 0; i <= nA; i++)
        {
            float a = a0 + (a1 - a0) * i / nA;
            for (int j = 0; j <= nB; j++)
            {
                float b = b0 + (b1 - b0) * j / nB;
                Vertices.Add(new Vector4(a, b, kind, aux));
            }
        }
        int stride = nB + 1;
        for (int i = 0; i < nA; i++)
        {
            for (int j = 0; j < nB; j++)
            {
                uint i00 = start + (uint)(i * stride + j);
                uint i01 = i00 + 1;
                uint i10 = i00 + (uint)stride;
                uint i11 = i10 + 1;
                Indices.Add(i00); Indices.Add(i10); Indices.Add(i01);
                Indices.Add(i01); Indices.Add(i10); Indices.Add(i11);
            }
        }
    }
}

/// <summary>Builds the shared procedural meshes. All shapes are finished on the GPU.</summary>
internal static class MeshBuilder
{
    /// <summary>Lofted fish: body ring grid plus dorsal, pectoral (x2), tail and anal fins.</summary>
    public static MeshData Fish()
    {
        var m = new MeshData();
        m.Grid(0, 0, 40, 14, 0f, 1f, 0f, MathF.Tau);   // body: u x theta
        m.Grid(1, 0, 10, 4, 0f, 1f, 0f, 1f);           // dorsal
        m.Grid(2, 1, 4, 4, 0f, 1f, 0f, 1f);            // pectoral (right)
        m.Grid(2, -1, 4, 4, 0f, 1f, 0f, 1f);           // pectoral (left)
        m.Grid(4, 0, 8, 8, 0f, 1f, 0f, 1f);            // tail
        m.Grid(5, 0, 8, 3, 0f, 1f, 0f, 1f);            // anal
        return m;
    }

    /// <summary>Flat winged body (top + bottom layer), tail strip and eight crustacean legs.</summary>
    public static MeshData Ray()
    {
        var m = new MeshData();
        m.Grid(0, 0, 28, 20, 0f, 1f, 0f, 1f);  // top
        m.Grid(0, 1, 28, 20, 0f, 1f, 0f, 1f);  // bottom
        m.Grid(1, 0, 12, 1, 0f, 1f, 0f, 1f);   // tail
        for (int leg = 0; leg < 8; leg++) m.Grid(2, leg, 6, 1, 0f, 1f, 0f, 1f);
        return m;
    }

    public const int TentacleCount = 10;

    /// <summary>Bell / mantle grid (angle x along) and ten tentacle ribbons.</summary>
    public static MeshData Jelly()
    {
        var m = new MeshData();
        m.Grid(0, 0, 28, 12, 0f, 1f, 0f, 1f);
        const int rows = 14;
        for (int k = 0; k < TentacleCount; k++)
        {
            float a = (k + 0.5f) / TentacleCount;
            uint start = (uint)m.Vertices.Count;
            for (int r = 0; r <= rows; r++)
            {
                float b = (float)r / rows;
                m.Vertices.Add(new Vector4(a, b, 1, -1));
                m.Vertices.Add(new Vector4(a, b, 1, 1));
            }
            for (int r = 0; r < rows; r++)
            {
                uint i0 = start + (uint)(r * 2);
                m.Indices.Add(i0); m.Indices.Add(i0 + 2); m.Indices.Add(i0 + 1);
                m.Indices.Add(i0 + 1); m.Indices.Add(i0 + 2); m.Indices.Add(i0 + 3);
            }
        }
        return m;
    }

    /// <summary>Ring tube used by every plant: (u along height, angle fraction, cap flag).</summary>
    public static MeshData Plant()
    {
        const int rows = 14;
        const int ring = 6;
        var m = new MeshData();
        for (int r = 0; r <= rows + 1; r++)
        {
            bool cap = r == rows + 1;
            float u = cap ? 1f : (float)r / rows;
            for (int s = 0; s <= ring; s++)
                m.Vertices.Add(new Vector4(u, (float)s / ring, cap ? 1f : 0f, 0f));
        }
        int stride = ring + 1;
        for (int r = 0; r <= rows; r++)
        {
            for (int s = 0; s < ring; s++)
            {
                uint i00 = (uint)(r * stride + s);
                uint i01 = i00 + 1;
                uint i10 = i00 + (uint)stride;
                uint i11 = i10 + 1;
                m.Indices.Add(i00); m.Indices.Add(i10); m.Indices.Add(i01);
                m.Indices.Add(i01); m.Indices.Add(i10); m.Indices.Add(i11);
            }
        }
        return m;
    }
}

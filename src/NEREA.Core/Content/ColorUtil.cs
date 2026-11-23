using System.Globalization;
using System.Numerics;

namespace NEREA.Core.Content;

public static class ColorUtil
{
    /// <summary>Parses #RRGGBB (or RRGGBB) into a 0..1 RGB vector. Invalid input yields mid grey.</summary>
    public static Vector3 Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return new Vector3(0.5f);
        var s = hex.AsSpan().Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..]; // ignore alpha (AARRGGBB)
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return new Vector3(0.5f);
        return new Vector3(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
    }

    public static (byte r, byte g, byte b) ToBytes(Vector3 c) =>
        ((byte)Math.Clamp(c.X * 255f, 0, 255), (byte)Math.Clamp(c.Y * 255f, 0, 255), (byte)Math.Clamp(c.Z * 255f, 0, 255));
}

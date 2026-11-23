namespace NEREA.Core.Models;

/// <summary>A single piece of music known to the library.</summary>
public sealed class Track
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public string Genre { get; set; } = "";
    public int TrackNumber { get; set; }
    public int Year { get; set; }
    public long DurationMs { get; set; }
    public string? ArtPath { get; set; }
    public string Folder { get; set; } = "";
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayed { get; set; }
    public int PlayCount { get; set; }
    public bool IsFavorite { get; set; }
    /// <summary>ReplayGain track gain in dB when present in the tags (NaN otherwise).</summary>
    public double ReplayGainDb { get; set; } = double.NaN;

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown album" : Album;
}

public sealed class Playlist
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public int TrackCount { get; set; }
}

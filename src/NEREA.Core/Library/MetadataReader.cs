using System.Security.Cryptography;
using System.Text;
using NEREA.Core.Models;
using NEREA.Core.Persistence;
using NEREA.Core.Utilities;

namespace NEREA.Core.Library;

/// <summary>
/// Reads tags and artwork with TagLib#. Never throws: an unreadable or corrupt file yields a
/// best-effort track built from the file name (or null when the file is unusable).
/// </summary>
public static class MetadataReader
{
    public static Track? Read(string path, string? artDirectory = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            var track = new Track
            {
                Path = info.FullName,
                Folder = info.DirectoryName ?? "",
                Title = System.IO.Path.GetFileNameWithoutExtension(path),
                DateAdded = DateTime.UtcNow
            };

            try
            {
                using var file = TagLib.File.Create(path);
                var tag = file.Tag;
                if (!string.IsNullOrWhiteSpace(tag.Title)) track.Title = tag.Title.Trim();
                track.Artist = (tag.FirstPerformer ?? tag.FirstAlbumArtist ?? "").Trim();
                track.AlbumArtist = (tag.FirstAlbumArtist ?? track.Artist).Trim();
                track.Album = (tag.Album ?? "").Trim();
                track.Genre = (tag.FirstGenre ?? "").Trim();
                track.TrackNumber = (int)tag.Track;
                track.Year = (int)tag.Year;
                track.DurationMs = (long)file.Properties.Duration.TotalMilliseconds;
                try { track.ReplayGainDb = tag.ReplayGainTrackGain; } catch { /* optional */ }

                if (tag.Pictures is { Length: > 0 })
                    track.ArtPath = SaveArtwork(tag.Pictures[0].Data.Data, track, artDirectory ?? AppPaths.ArtDirectory);
            }
            catch (Exception ex)
            {
                // Corrupt / exotic tags: keep the file playable with file-name metadata
                Log.Warn($"Tags unreadable for '{System.IO.Path.GetFileName(path)}'.", ex);
            }

            return track;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not inspect '{path}'.", ex);
            return null;
        }
    }

    private static string? SaveArtwork(byte[] data, Track track, string directory)
    {
        try
        {
            if (data.Length == 0) return null;
            // One image per album, not per track
            var key = $"{track.AlbumArtist}|{track.Album}";
            if (string.IsNullOrWhiteSpace(track.Album)) key = track.Path;
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            var ext = IsPng(data) ? ".png" : ".jpg";
            var file = System.IO.Path.Combine(directory, hash + ext);
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(file, data);
            }
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn("Artwork could not be cached.", ex);
            return null;
        }
    }

    private static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
}

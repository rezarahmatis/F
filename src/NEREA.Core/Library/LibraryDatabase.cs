using Microsoft.Data.Sqlite;
using NEREA.Core.Models;
using NEREA.Core.Utilities;

namespace NEREA.Core.Library;

/// <summary>
/// SQLite persistence for the music library and playlists. SQLite is used because track metadata,
/// favourites, play history and playlists are genuinely relational and can reach tens of thousands of rows.
/// All methods are synchronous and fast; callers run them on background threads.
/// </summary>
public sealed class LibraryDatabase
{
    private readonly string path;
    private readonly string connectionString;

    private const string TrackColumns =
        "id, path, title, artist, album, album_artist, genre, track_no, year, duration_ms, art_path, folder, date_added, last_played, play_count, favorite, replay_gain";

    public LibraryDatabase(string path)
    {
        this.path = path;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    /// <summary>Creates the schema. A corrupt database file is set aside and rebuilt.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            CreateSchema();
        }
        catch (SqliteException ex)
        {
            Log.Error("Library database is damaged; rebuilding.", ex);
            SqliteConnection.ClearAllPools();
            try { File.Move(path, path + ".corrupt", true); } catch { /* ignore */ }
            CreateSchema();
        }
    }

    private void CreateSchema()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, @"
CREATE TABLE IF NOT EXISTS tracks(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  path TEXT NOT NULL UNIQUE,
  title TEXT NOT NULL, artist TEXT NOT NULL DEFAULT '', album TEXT NOT NULL DEFAULT '',
  album_artist TEXT NOT NULL DEFAULT '', genre TEXT NOT NULL DEFAULT '',
  track_no INTEGER NOT NULL DEFAULT 0, year INTEGER NOT NULL DEFAULT 0,
  duration_ms INTEGER NOT NULL DEFAULT 0, art_path TEXT, folder TEXT NOT NULL DEFAULT '',
  date_added TEXT NOT NULL, last_played TEXT, play_count INTEGER NOT NULL DEFAULT 0,
  favorite INTEGER NOT NULL DEFAULT 0, replay_gain REAL);
CREATE TABLE IF NOT EXISTS playlists(
  id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, created TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS playlist_items(
  playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
  position INTEGER NOT NULL,
  track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
  PRIMARY KEY(playlist_id, position));
CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album);
CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist);
CREATE INDEX IF NOT EXISTS ix_tracks_played ON tracks(last_played);");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ───────────────────────── tracks ─────────────────────────

    public HashSet<string> GetKnownPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM tracks";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>Inserts or updates tracks in one transaction. Existing favourites and play counts are preserved.</summary>
    public int UpsertMany(IEnumerable<Track> tracks)
    {
        int n = 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO tracks(path,title,artist,album,album_artist,genre,track_no,year,duration_ms,art_path,folder,date_added,replay_gain)
VALUES($path,$title,$artist,$album,$aa,$genre,$tn,$year,$dur,$art,$folder,$added,$rg)
ON CONFLICT(path) DO UPDATE SET title=excluded.title, artist=excluded.artist, album=excluded.album,
  album_artist=excluded.album_artist, genre=excluded.genre, track_no=excluded.track_no, year=excluded.year,
  duration_ms=excluded.duration_ms, art_path=excluded.art_path, folder=excluded.folder, replay_gain=excluded.replay_gain;";
        foreach (var name in new[] { "$path", "$title", "$artist", "$album", "$aa", "$genre", "$tn", "$year", "$dur", "$art", "$folder", "$added", "$rg" })
            cmd.Parameters.Add(new SqliteParameter(name, null));

        foreach (var t in tracks)
        {
            cmd.Parameters["$path"].Value = t.Path;
            cmd.Parameters["$title"].Value = t.Title;
            cmd.Parameters["$artist"].Value = t.Artist;
            cmd.Parameters["$album"].Value = t.Album;
            cmd.Parameters["$aa"].Value = t.AlbumArtist;
            cmd.Parameters["$genre"].Value = t.Genre;
            cmd.Parameters["$tn"].Value = t.TrackNumber;
            cmd.Parameters["$year"].Value = t.Year;
            cmd.Parameters["$dur"].Value = t.DurationMs;
            cmd.Parameters["$art"].Value = (object?)t.ArtPath ?? DBNull.Value;
            cmd.Parameters["$folder"].Value = t.Folder;
            cmd.Parameters["$added"].Value = t.DateAdded.ToString("O");
            cmd.Parameters["$rg"].Value = double.IsNaN(t.ReplayGainDb) ? DBNull.Value : t.ReplayGainDb;
            cmd.ExecuteNonQuery();
            n++;
        }
        tx.Commit();
        return n;
    }

    public List<Track> GetAllTracks()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TrackColumns} FROM tracks";
        return ReadTracks(cmd);
    }

    public List<Track> GetTracks(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        var result = new Dictionary<long, Track>();
        using var c = Open();
        foreach (var chunk in list.Chunk(400))
        {
            using var cmd = c.CreateCommand();
            var names = chunk.Select((_, i) => "$i" + i).ToArray();
            cmd.CommandText = $"SELECT {TrackColumns} FROM tracks WHERE id IN ({string.Join(",", names)})";
            for (int i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
            foreach (var t in ReadTracks(cmd)) result[t.Id] = t;
        }
        // keep the caller's order, drop unknown ids
        return list.Where(result.ContainsKey).Select(id => result[id]).ToList();
    }

    public void SetFavorite(long id, bool favorite)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET favorite=$f WHERE id=$id";
        cmd.Parameters.AddWithValue("$f", favorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkPlayed(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE tracks SET last_played=$t, play_count=play_count+1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void RemoveTracks(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM tracks WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ───────────────────────── playlists ─────────────────────────

    public List<Playlist> GetPlaylists()
    {
        var list = new List<Playlist>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT p.id, p.name, p.created, (SELECT COUNT(*) FROM playlist_items i WHERE i.playlist_id=p.id)
                            FROM playlists p ORDER BY p.name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Playlist { Id = r.GetInt64(0), Name = r.GetString(1), Created = ParseDate(r.GetString(2)), TrackCount = r.GetInt32(3) });
        return list;
    }

    public long CreatePlaylist(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists(name, created) VALUES($n,$c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void RenamePlaylist(long id, string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$n", name.Trim());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeletePlaylist(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_items WHERE playlist_id=$id; DELETE FROM playlists WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void AddToPlaylist(long playlistId, IEnumerable<long> trackIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        long next;
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT COALESCE(MAX(position)+1, 0) FROM playlist_items WHERE playlist_id=$p";
            q.Parameters.AddWithValue("$p", playlistId);
            next = (long)(q.ExecuteScalar() ?? 0L);
        }
        foreach (var id in trackIds)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO playlist_items(playlist_id, position, track_id) VALUES($p,$pos,$t)";
            cmd.Parameters.AddWithValue("$p", playlistId);
            cmd.Parameters.AddWithValue("$pos", next++);
            cmd.Parameters.AddWithValue("$t", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void RemoveFromPlaylist(long playlistId, long trackId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, $"DELETE FROM playlist_items WHERE playlist_id={playlistId} AND track_id={trackId}", tx);
        // keep positions contiguous
        Exec(c, $@"CREATE TEMP TABLE IF NOT EXISTS _pl(rn INTEGER, tid INTEGER);
                   DELETE FROM _pl;
                   INSERT INTO _pl SELECT ROW_NUMBER() OVER (ORDER BY position)-1, track_id FROM playlist_items WHERE playlist_id={playlistId};
                   DELETE FROM playlist_items WHERE playlist_id={playlistId};
                   INSERT INTO playlist_items(playlist_id, position, track_id) SELECT {playlistId}, rn, tid FROM _pl;", tx);
        tx.Commit();
    }

    public List<long> GetPlaylistTrackIds(long playlistId)
    {
        var ids = new List<long>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT track_id FROM playlist_items WHERE playlist_id=$p ORDER BY position";
        cmd.Parameters.AddWithValue("$p", playlistId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    // ───────────────────────── helpers ─────────────────────────

    private static List<Track> ReadTracks(SqliteCommand cmd)
    {
        var list = new List<Track>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Track
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                Title = r.GetString(2),
                Artist = r.GetString(3),
                Album = r.GetString(4),
                AlbumArtist = r.GetString(5),
                Genre = r.GetString(6),
                TrackNumber = r.GetInt32(7),
                Year = r.GetInt32(8),
                DurationMs = r.GetInt64(9),
                ArtPath = r.IsDBNull(10) ? null : r.GetString(10),
                Folder = r.GetString(11),
                DateAdded = ParseDate(r.GetString(12)),
                LastPlayed = r.IsDBNull(13) ? null : ParseDate(r.GetString(13)),
                PlayCount = r.GetInt32(14),
                IsFavorite = r.GetInt32(15) != 0,
                ReplayGainDb = r.IsDBNull(16) ? double.NaN : r.GetDouble(16)
            });
        }
        return list;
    }

    private static DateTime ParseDate(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.UtcNow;
}

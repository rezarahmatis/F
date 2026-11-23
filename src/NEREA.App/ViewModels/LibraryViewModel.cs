using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using NEREA.App.Services;
using NEREA.Core.Library;
using NEREA.Core.Models;
using NEREA.Core.Utilities;

namespace NEREA.App.ViewModels;

public enum LibraryTab { Songs, Albums, Artists, Playlists, Favorites, Recent, Folders, Queue }
public enum LibraryRowKind { Track, Group, Playlist }

/// <summary>One line of the library list: a song, an album / artist / folder, or a playlist.</summary>
public sealed partial class LibraryRow : ObservableObject
{
    public LibraryRowKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Key { get; init; } = "";
    public long PlaylistId { get; init; }
    public Track? Track { get; init; }
    public bool IsTrack => Kind == LibraryRowKind.Track;

    [ObservableProperty] private bool isFavorite;
}

/// <summary>Songs, albums, artists, playlists, favourites, recents and folders with search, sort and import.</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryDatabase db;
    private readonly LibraryScanner scanner;
    private readonly PlayerViewModel player;
    private readonly DialogService dialogs;
    private readonly FilePickerService pickers;
    private readonly DispatcherQueue ui;

    private List<Track> all = new();
    private List<Track> visibleTracks = new();
    private int refreshVersion;
    private string? drillKey;
    private CancellationTokenSource? importCts;

    public IReadOnlyList<string> SortOptions { get; } = new[] { "Title", "Artist", "Album", "Recently added", "Duration" };
    public IReadOnlyList<string> TabNames { get; } = new[] { "Songs", "Albums", "Artists", "Playlists", "Favorites", "Recent", "Folders", "Queue" };

    [ObservableProperty] private IReadOnlyList<LibraryRow> rows = Array.Empty<LibraryRow>();
    [ObservableProperty] private IReadOnlyList<Playlist> playlists = Array.Empty<Playlist>();
    [ObservableProperty] private int tabIndex;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private string drillTitle = "";
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool isImporting;
    [ObservableProperty] private double importProgress;
    [ObservableProperty] private string importStatus = "";
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private bool isEmpty = true;
    [ObservableProperty] private string emptyText = "Your library is empty.\nImport songs or a folder, or drop them anywhere in NEREA.";

    public LibraryTab Tab => (LibraryTab)Math.Clamp(TabIndex, 0, 7);
    public bool IsPlaylistsTab => Tab == LibraryTab.Playlists && drillKey == null;

    public event Action<string>? Notice;
    /// <summary>Raised after an import so the shell can react (e.g. start playing dropped music).</summary>
    public event Action<IReadOnlyList<Track>>? ImportCompleted;

    public LibraryViewModel(LibraryDatabase db, LibraryScanner scanner, PlayerViewModel player,
        DialogService dialogs, FilePickerService pickers, DispatcherQueue ui)
    {
        this.db = db;
        this.scanner = scanner;
        this.player = player;
        this.dialogs = dialogs;
        this.pickers = pickers;
        this.ui = ui;

        player.FavoriteChanged += OnFavoriteChanged;
        player.Queue.CollectionChanged += (_, _) => { if (Tab == LibraryTab.Queue) Refresh(); };
        player.TrackChanged += _ => { if (Tab == LibraryTab.Recent) Refresh(); };
    }

    public async Task InitializeAsync()
    {
        await ReloadAsync();
        player.RestoreFrom(all);
    }

    private async Task ReloadAsync()
    {
        try
        {
            var (tracks, lists) = await Task.Run(() => (db.GetAllTracks(), db.GetPlaylists()));
            all = tracks;
            Playlists = lists;
        }
        catch (Exception ex)
        {
            Log.Error("Library could not be loaded.", ex);
            Notice?.Invoke("Your music library could not be read. It will be rebuilt as you import music.");
            all = new List<Track>();
        }
        Refresh();
    }

    // ───────────────────────────── view building ─────────────────────────────

    partial void OnTabIndexChanged(int value)
    {
        drillKey = null;
        DrillTitle = "";
        CanGoBack = false;
        OnPropertyChanged(nameof(IsPlaylistsTab));
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSortIndexChanged(int value) => Refresh();

    public async void Refresh()
    {
        int version = ++refreshVersion;
        var tab = Tab;
        var search = SearchText.Trim();
        var sort = SortIndex;
        var key = drillKey;
        var snapshot = all;
        var queue = player.Queue.ToList();

        List<LibraryRow> built;
        List<Track> tracks;
        try
        {
            (built, tracks) = await Task.Run(() => Build(snapshot, queue, tab, search, sort, key));
        }
        catch (Exception ex)
        {
            Log.Warn("Library view could not be built.", ex);
            return;
        }
        if (version != refreshVersion) return; // a newer refresh superseded this one

        visibleTracks = tracks;
        Rows = built;
        IsEmpty = built.Count == 0;
        EmptyText = all.Count == 0
            ? "Your library is empty.\nImport songs or a folder, or drop them anywhere in NEREA."
            : search.Length > 0 ? "Nothing matches your search." : "Nothing here yet.";
        Summary = all.Count == 0 ? "" : $"{all.Count:N0} songs";
    }

    private (List<LibraryRow>, List<Track>) Build(List<Track> src, List<Track> queue, LibraryTab tab, string search, int sort, string? key)
    {
        IEnumerable<Track> q = src;
        bool Match(Track t) =>
            search.Length == 0 ||
            t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            t.Artist.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            t.Album.Contains(search, StringComparison.OrdinalIgnoreCase);

        List<Track> TrackList(IEnumerable<Track> seq) => Sorted(seq.Where(Match), sort).ToList();

        // Drilled into a group or playlist: show its songs
        if (key != null)
        {
            IEnumerable<Track> inner = tab switch
            {
                LibraryTab.Albums => src.Where(t => AlbumKey(t) == key).OrderBy(t => t.TrackNumber).ThenBy(t => t.Title),
                LibraryTab.Artists => src.Where(t => t.DisplayArtist == key).OrderBy(t => t.Album).ThenBy(t => t.TrackNumber),
                LibraryTab.Folders => src.Where(t => t.Folder == key).OrderBy(t => t.Title),
                LibraryTab.Playlists => PlaylistTracks(src, long.Parse(key)),
                _ => src
            };
            var list = inner.Where(Match).ToList();
            return (list.Select(t => TrackRow(t)).ToList(), list);
        }

        switch (tab)
        {
            case LibraryTab.Songs:
            {
                var list = TrackList(src);
                return (list.Select(t => TrackRow(t)).ToList(), list);
            }
            case LibraryTab.Favorites:
            {
                var list = TrackList(src.Where(t => t.IsFavorite));
                return (list.Select(t => TrackRow(t)).ToList(), list);
            }
            case LibraryTab.Recent:
            {
                var list = src.Where(t => t.LastPlayed != null && Match(t)).OrderByDescending(t => t.LastPlayed).Take(100).ToList();
                return (list.Select(t => TrackRow(t)).ToList(), list);
            }
            case LibraryTab.Queue:
            {
                var list = queue.Where(Match).ToList();
                return (list.Select(t => TrackRow(t)).ToList(), list);
            }
            case LibraryTab.Albums:
            {
                var groups = src.Where(Match).GroupBy(AlbumKey).OrderBy(g => g.First().DisplayAlbum, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new LibraryRow
                    {
                        Kind = LibraryRowKind.Group, Key = g.Key, Title = g.First().DisplayAlbum,
                        Subtitle = $"{ArtistOf(g.First())} · {g.Count()} songs"
                    }).ToList();
                return (groups, new List<Track>());
            }
            case LibraryTab.Artists:
            {
                var groups = src.Where(Match).GroupBy(t => t.DisplayArtist).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new LibraryRow
                    {
                        Kind = LibraryRowKind.Group, Key = g.Key, Title = g.Key,
                        Subtitle = $"{g.Select(t => t.Album).Distinct().Count()} albums · {g.Count()} songs"
                    }).ToList();
                return (groups, new List<Track>());
            }
            case LibraryTab.Folders:
            {
                var groups = src.Where(Match).GroupBy(t => t.Folder).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => new LibraryRow
                    {
                        Kind = LibraryRowKind.Group, Key = g.Key,
                        Title = new DirectoryInfo(g.Key).Name is { Length: > 0 } n ? n : g.Key,
                        Subtitle = $"{g.Key} · {g.Count()} songs"
                    }).ToList();
                return (groups, new List<Track>());
            }
            default: // Playlists
            {
                var lists = db.GetPlaylists();
                var rowsOut = lists.Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new LibraryRow
                    {
                        Kind = LibraryRowKind.Playlist, PlaylistId = p.Id, Key = p.Id.ToString(), Title = p.Name,
                        Subtitle = $"{p.TrackCount} songs"
                    }).ToList();
                return (rowsOut, new List<Track>());
            }
        }
    }

    private IEnumerable<Track> PlaylistTracks(List<Track> src, long playlistId)
    {
        var byId = src.ToDictionary(t => t.Id);
        return db.GetPlaylistTrackIds(playlistId).Where(byId.ContainsKey).Select(id => byId[id]);
    }

    private static string AlbumKey(Track t) => $"{(string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist)}|{t.Album}";
    private static string ArtistOf(Track t) => string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.DisplayArtist : t.AlbumArtist;

    private static IEnumerable<Track> Sorted(IEnumerable<Track> seq, int sort) => sort switch
    {
        1 => seq.OrderBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Album).ThenBy(t => t.TrackNumber),
        2 => seq.OrderBy(t => t.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.TrackNumber),
        3 => seq.OrderByDescending(t => t.DateAdded),
        4 => seq.OrderByDescending(t => t.DurationMs),
        _ => seq.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
    };

    private static LibraryRow TrackRow(Track t) => new()
    {
        Kind = LibraryRowKind.Track,
        Track = t,
        Title = t.Title,
        Subtitle = string.IsNullOrWhiteSpace(t.Album) ? t.DisplayArtist : $"{t.DisplayArtist} · {t.Album}",
        Detail = t.DurationMs > 0 ? PlayerViewModel.Format(t.Duration.TotalSeconds) : "",
        IsFavorite = t.IsFavorite
    };

    // ───────────────────────────── actions ─────────────────────────────

    public void Activate(LibraryRow row)
    {
        switch (row.Kind)
        {
            case LibraryRowKind.Track:
                int index = visibleTracks.FindIndex(t => ReferenceEquals(t, row.Track));
                if (Tab == LibraryTab.Queue) player.PlayQueueIndex(index);
                else if (index >= 0) player.PlayQueue(visibleTracks, index);
                break;
            default:
                drillKey = row.Key;
                DrillTitle = row.Title;
                CanGoBack = true;
                OnPropertyChanged(nameof(IsPlaylistsTab));
                Refresh();
                break;
        }
    }

    [RelayCommand]
    public void Back()
    {
        drillKey = null;
        DrillTitle = "";
        CanGoBack = false;
        OnPropertyChanged(nameof(IsPlaylistsTab));
        Refresh();
    }

    [RelayCommand]
    public void PlayAll()
    {
        if (visibleTracks.Count > 0) player.PlayQueue(visibleTracks, 0);
    }

    [RelayCommand]
    public void ShuffleAll()
    {
        if (visibleTracks.Count == 0) return;
        player.Shuffle = true;
        player.PlayQueue(visibleTracks, Random.Shared.Next(visibleTracks.Count));
    }

    private List<Track> TracksOf(LibraryRow row)
    {
        if (row.Kind == LibraryRowKind.Track) return row.Track != null ? new List<Track> { row.Track } : new();
        var tab = Tab;
        if (row.Kind == LibraryRowKind.Playlist) return PlaylistTracks(all, row.PlaylistId).ToList();
        return tab switch
        {
            LibraryTab.Albums => all.Where(t => AlbumKey(t) == row.Key).OrderBy(t => t.TrackNumber).ToList(),
            LibraryTab.Artists => all.Where(t => t.DisplayArtist == row.Key).OrderBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList(),
            _ => all.Where(t => t.Folder == row.Key).OrderBy(t => t.Title).ToList()
        };
    }

    public void PlayRow(LibraryRow row)
    {
        var list = TracksOf(row);
        if (list.Count > 0) player.PlayQueue(list, 0);
    }

    public void PlayNextRow(LibraryRow row) => player.PlayNext(TracksOf(row));
    public void EnqueueRow(LibraryRow row) => player.Enqueue(TracksOf(row));

    public void ToggleFavorite(LibraryRow row)
    {
        if (row.Track != null) player.ToggleFavorite(row.Track);
    }

    private void OnFavoriteChanged(Track track)
    {
        foreach (var r in Rows)
            if (ReferenceEquals(r.Track, track)) r.IsFavorite = track.IsFavorite;
        if (Tab == LibraryTab.Favorites) Refresh();
    }

    public async void AddToPlaylist(LibraryRow row, long playlistId)
    {
        var ids = TracksOf(row).Select(t => t.Id).ToList();
        if (ids.Count == 0) return;
        await Task.Run(() => db.AddToPlaylist(playlistId, ids));
        await RefreshPlaylistsAsync();
        Notice?.Invoke(ids.Count == 1 ? "Added to playlist" : $"{ids.Count} songs added to playlist");
    }

    public async Task<long?> NewPlaylistWithAsync(LibraryRow? row)
    {
        var name = await dialogs.PromptTextAsync("New playlist", "Playlist name", "", "Create");
        if (name == null) return null;
        long id = await Task.Run(() => db.CreatePlaylist(name));
        if (row != null) await Task.Run(() => db.AddToPlaylist(id, TracksOf(row).Select(t => t.Id)));
        await RefreshPlaylistsAsync();
        return id;
    }

    [RelayCommand]
    public async Task NewPlaylistAsync() => await NewPlaylistWithAsync(null);

    public async Task RenamePlaylistAsync(LibraryRow row)
    {
        var name = await dialogs.PromptTextAsync("Rename playlist", "Playlist name", row.Title, "Rename");
        if (name == null) return;
        await Task.Run(() => db.RenamePlaylist(row.PlaylistId, name));
        await RefreshPlaylistsAsync();
    }

    public async Task DeletePlaylistAsync(LibraryRow row)
    {
        if (!await dialogs.ConfirmAsync("Delete playlist", $"Delete “{row.Title}”? The songs stay in your library.")) return;
        await Task.Run(() => db.DeletePlaylist(row.PlaylistId));
        await RefreshPlaylistsAsync();
    }

    public async void RemoveFromPlaylist(LibraryRow row)
    {
        if (drillKey == null || Tab != LibraryTab.Playlists || row.Track == null) return;
        long pid = long.Parse(drillKey);
        await Task.Run(() => db.RemoveFromPlaylist(pid, row.Track.Id));
        Refresh();
    }

    public async void RemoveFromLibrary(LibraryRow row)
    {
        var ids = TracksOf(row).Select(t => t.Id).ToList();
        if (ids.Count == 0) return;
        await Task.Run(() => db.RemoveTracks(ids));
        await ReloadAsync();
    }

    public void ShowInExplorer(LibraryRow row)
    {
        try
        {
            var path = row.Track?.Path ?? TracksOf(row).FirstOrDefault()?.Path;
            if (path != null) Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex) { Log.Warn("Explorer could not be opened.", ex); }
    }

    private async Task RefreshPlaylistsAsync()
    {
        Playlists = await Task.Run(() => db.GetPlaylists());
        if (Tab == LibraryTab.Playlists) Refresh();
    }

    // ───────────────────────────── import ─────────────────────────────

    [RelayCommand]
    public async Task ImportFilesAsync()
    {
        var files = await pickers.PickMusicFilesAsync();
        if (files.Count > 0) await ImportPathsAsync(files, playImported: false);
    }

    [RelayCommand]
    public async Task ImportFolderAsync()
    {
        var folder = await pickers.PickFolderAsync();
        if (folder != null) await ImportPathsAsync(new[] { folder }, playImported: false);
    }

    [RelayCommand]
    public void CancelImport() => importCts?.Cancel();

    public async Task ImportPathsAsync(IEnumerable<string> paths, bool playImported)
    {
        if (IsImporting) { Notice?.Invoke("An import is already running"); return; }
        var pathList = paths.ToList();
        var expected = await Task.Run(() => LibraryScanner.Enumerate(pathList).ToList());
        if (expected.Count == 0) { Notice?.Invoke("No supported music files were found"); return; }

        IsImporting = true;
        ImportProgress = 0;
        ImportStatus = "Reading music…";
        importCts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p => ui.TryEnqueue(() =>
        {
            ImportProgress = p.Total == 0 ? 1 : (double)p.Processed / p.Total;
            ImportStatus = p.Total == 0 ? "" : $"{p.Processed} / {p.Total}";
        }));

        try
        {
            var result = await scanner.ImportAsync(pathList, progress, importCts.Token);
            await ReloadAsync();
            Notice?.Invoke(result.Summary);

            var set = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
            var imported = all.Where(t => set.Contains(t.Path)).OrderBy(t => t.Folder).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title).ToList();
            if (playImported && imported.Count > 0) player.PlayQueue(imported, 0);
            ImportCompleted?.Invoke(imported);
        }
        catch (Exception ex)
        {
            Log.Error("Import failed.", ex);
            Notice?.Invoke("The import could not be completed.");
        }
        finally
        {
            IsImporting = false;
            importCts?.Dispose();
            importCts = null;
        }
    }
}

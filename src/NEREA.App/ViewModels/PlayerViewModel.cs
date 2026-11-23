using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NEREA.App.Services;
using NEREA.Audio;
using NEREA.Core.Library;
using NEREA.Core.Models;
using NEREA.Core.Utilities;

namespace NEREA.App.ViewModels;

/// <summary>Queue, transport, shuffle / repeat and the "now playing" state. Playback itself lives in <see cref="AudioEngine"/>.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly AudioEngine engine;
    private readonly LibraryDatabase db;
    private readonly SettingsService settings;
    private readonly DispatcherQueue ui;
    private readonly DispatcherQueueTimer timer;

    private List<int> order = new();
    private int orderPos = -1;
    private int consecutiveFailures;
    private DateTime lastPersist = DateTime.MinValue;
    private readonly Random rng = new();

    public ObservableCollection<Track> Queue { get; } = new();

    [ObservableProperty] private Track? currentTrack;
    [ObservableProperty] private string titleText = "";
    [ObservableProperty] private string artistText = "";
    [ObservableProperty] private ImageSource? art;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private bool isFavorite;
    [ObservableProperty] private double positionSeconds;
    [ObservableProperty] private double durationSeconds = 1;
    [ObservableProperty] private string positionText = "0:00";
    [ObservableProperty] private string durationText = "0:00";
    [ObservableProperty] private double volume;
    [ObservableProperty] private bool isMuted;
    [ObservableProperty] private bool shuffle;
    [ObservableProperty] private RepeatMode repeat;

    /// <summary>True while the user drags the progress slider (position updates pause).</summary>
    public bool UserSeeking { get; set; }
    public bool HasTrack => CurrentTrack != null;
    public bool HasArt => Art != null;
    public bool IsRepeating => Repeat != RepeatMode.Off;
    public string RepeatGlyph => Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";

    public event Action<Track?>? TrackChanged;
    public event Action<string>? Notice;
    public event Action<Track>? FavoriteChanged;
    public event Action? PlaybackStateChanged;

    public PlayerViewModel(AudioEngine engine, LibraryDatabase db, SettingsService settings, DispatcherQueue ui)
    {
        this.engine = engine;
        this.db = db;
        this.settings = settings;
        this.ui = ui;

        var s = settings.Current;
        volume = s.Volume;
        isMuted = s.Muted;
        shuffle = s.Shuffle;
        repeat = s.Repeat;
        engine.Volume = volume;
        engine.Muted = isMuted;
        engine.NormalizeVolume = s.NormalizeVolume;
        engine.SetDevice(s.OutputDeviceId);

        engine.StateChanged += state => ui.TryEnqueue(() =>
        {
            IsPlaying = state == EnginePlaybackState.Playing;
            PlaybackStateChanged?.Invoke();
        });
        engine.TrackEnded += () => ui.TryEnqueue(() => Next(auto: true));
        engine.Error += msg => ui.TryEnqueue(() => Notice?.Invoke(msg));

        timer = ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    // ───────────────────────────── queue management ─────────────────────────────

    public void PlayQueue(IReadOnlyList<Track> tracks, int startIndex)
    {
        if (tracks.Count == 0) return;
        Queue.Clear();
        foreach (var t in tracks) Queue.Add(t);
        consecutiveFailures = 0;
        RebuildOrder(Math.Clamp(startIndex, 0, Queue.Count - 1));
        StartAt(Math.Clamp(startIndex, 0, Queue.Count - 1));
    }

    public void PlayNext(IEnumerable<Track> tracks)
    {
        int insertAt = CurrentQueueIndex() + 1;
        foreach (var t in tracks) Queue.Insert(Math.Min(insertAt++, Queue.Count), t);
        RebuildOrder(CurrentQueueIndex());
        Notice?.Invoke("Playing next");
    }

    public void Enqueue(IEnumerable<Track> tracks)
    {
        bool wasEmpty = Queue.Count == 0;
        foreach (var t in tracks) Queue.Add(t);
        RebuildOrder(CurrentQueueIndex());
        if (wasEmpty && Queue.Count > 0) StartAt(0);
        else Notice?.Invoke("Added to queue");
    }

    public void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        consecutiveFailures = 0;
        RebuildOrder(index);
        StartAt(index);
    }

    private int CurrentQueueIndex() => orderPos >= 0 && orderPos < order.Count ? order[orderPos] : -1;

    private void RebuildOrder(int currentIndex)
    {
        order = Enumerable.Range(0, Queue.Count).ToList();
        if (Shuffle)
        {
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            int at = order.IndexOf(currentIndex);
            if (at > 0) (order[0], order[at]) = (order[at], order[0]);
        }
        orderPos = Math.Max(0, order.IndexOf(currentIndex));
        if (currentIndex < 0) orderPos = -1;
    }

    // ───────────────────────────── transport ─────────────────────────────

    private void StartAt(int queueIndex)
    {
        if (queueIndex < 0 || queueIndex >= Queue.Count) return;
        var track = Queue[queueIndex];
        orderPos = order.IndexOf(queueIndex);

        if (!engine.Load(track.Path, 0, track.ReplayGainDb))
        {
            // Bad or missing file: say so once, then move on - never stall or crash.
            consecutiveFailures++;
            if (consecutiveFailures < Queue.Count) Next(auto: true);
            else { consecutiveFailures = 0; SetCurrent(null); }
            return;
        }

        consecutiveFailures = 0;
        SetCurrent(track);
        engine.Play();
        _ = Task.Run(() => { try { db.MarkPlayed(track.Id); } catch (Exception ex) { Log.Warn("MarkPlayed failed.", ex); } });
        Persist(force: true);
    }

    private void SetCurrent(Track? track)
    {
        CurrentTrack = track;
        TitleText = track?.Title ?? "";
        ArtistText = track?.DisplayArtist ?? "";
        IsFavorite = track?.IsFavorite ?? false;
        PositionSeconds = 0;
        PositionText = "0:00";
        DurationSeconds = Math.Max(1, track?.Duration.TotalSeconds ?? 1);
        DurationText = Format(DurationSeconds);
        Art = LoadArt(track?.ArtPath);
        OnPropertyChanged(nameof(HasTrack));
        TrackChanged?.Invoke(track);
    }

    private static ImageSource? LoadArt(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return new BitmapImage(new Uri(path)) { DecodePixelWidth = 96 };
        }
        catch { return null; }
    }

    [RelayCommand]
    public void PlayPause()
    {
        if (!engine.HasTrack)
        {
            if (Queue.Count > 0) StartAt(Math.Max(0, CurrentQueueIndex()));
            else Notice?.Invoke("Add music with Ctrl+O, or drop files here");
            return;
        }
        if (engine.State == EnginePlaybackState.Playing) engine.Pause(); else engine.Play();
    }

    [RelayCommand]
    public void Next() => Next(auto: false);

    public void Next(bool auto)
    {
        if (Queue.Count == 0) return;
        if (auto && Repeat == RepeatMode.One && CurrentQueueIndex() >= 0) { StartAt(CurrentQueueIndex()); return; }

        int np = orderPos + 1;
        if (np >= order.Count)
        {
            if (auto && Repeat == RepeatMode.Off)
            {
                engine.Stop();
                SetCurrent(null);
                return;
            }
            if (Shuffle) RebuildOrder(-1);
            np = 0;
        }
        StartAt(order[np]);
    }

    [RelayCommand]
    public void Previous()
    {
        if (Queue.Count == 0) return;
        if (engine.HasTrack && engine.Position.TotalSeconds > 3) { Seek(0); return; }
        int np = orderPos - 1;
        if (np < 0) np = order.Count - 1;
        StartAt(order[np]);
    }

    public void Seek(double seconds)
    {
        engine.Seek(seconds);
        PositionSeconds = seconds;
        PositionText = Format(seconds);
    }

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    public void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand]
    public void CycleRepeat() =>
        Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };

    [RelayCommand]
    public void ToggleCurrentFavorite()
    {
        if (CurrentTrack != null) ToggleFavorite(CurrentTrack);
    }

    public void ToggleFavorite(Track track)
    {
        track.IsFavorite = !track.IsFavorite;
        var id = track.Id;
        var value = track.IsFavorite;
        _ = Task.Run(() => { try { db.SetFavorite(id, value); } catch (Exception ex) { Log.Warn("Favourite not saved.", ex); } });
        if (ReferenceEquals(track, CurrentTrack)) IsFavorite = value;
        FavoriteChanged?.Invoke(track);
    }

    partial void OnVolumeChanged(double value)
    {
        engine.Volume = value;
        if (value > 0.001 && IsMuted) IsMuted = false;
        settings.Current.Volume = value;
        settings.SaveSoon();
    }

    partial void OnIsMutedChanged(bool value)
    {
        engine.Muted = value;
        settings.Current.Muted = value;
        settings.SaveSoon();
    }

    partial void OnShuffleChanged(bool value)
    {
        settings.Current.Shuffle = value;
        settings.SaveSoon();
        if (Queue.Count > 0) RebuildOrder(CurrentQueueIndex());
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        settings.Current.Repeat = value;
        settings.SaveSoon();
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(IsRepeating));
    }

    partial void OnArtChanged(ImageSource? value) => OnPropertyChanged(nameof(HasArt));

    // ───────────────────────────── restore & persist ─────────────────────────────

    /// <summary>Restores the last queue and position (paused) using the library's track objects.</summary>
    public void RestoreFrom(IReadOnlyList<Track> library)
    {
        try
        {
            var s = settings.Current;
            if (s.QueueTrackIds.Count == 0) return;
            var byId = library.ToDictionary(t => t.Id);
            var tracks = s.QueueTrackIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (tracks.Count == 0) return;

            Queue.Clear();
            foreach (var t in tracks) Queue.Add(t);
            int index = Math.Clamp(s.QueueIndex, 0, Queue.Count - 1);
            RebuildOrder(index);
            var track = Queue[index];
            if (engine.Load(track.Path, s.PositionSeconds, track.ReplayGainDb))
            {
                SetCurrent(track);
                PositionSeconds = s.PositionSeconds;
                PositionText = Format(s.PositionSeconds);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Previous session could not be restored.", ex);
        }
    }

    public void Persist(bool force = false)
    {
        if (!force && (DateTime.UtcNow - lastPersist).TotalSeconds < 5) return;
        lastPersist = DateTime.UtcNow;
        var s = settings.Current;
        s.QueueTrackIds = Queue.Select(t => t.Id).ToList();
        s.QueueIndex = CurrentQueueIndex();
        s.PositionSeconds = engine.HasTrack ? engine.Position.TotalSeconds : 0;
        settings.SaveSoon();
    }

    private void Tick()
    {
        if (!engine.HasTrack || UserSeeking) return;
        double pos = engine.Position.TotalSeconds;
        double dur = engine.Duration.TotalSeconds;
        if (dur > 1 && Math.Abs(dur - DurationSeconds) > 0.5) { DurationSeconds = dur; DurationText = Format(dur); }
        PositionSeconds = pos;
        PositionText = Format(pos);
        if (IsPlaying) Persist();
    }

    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

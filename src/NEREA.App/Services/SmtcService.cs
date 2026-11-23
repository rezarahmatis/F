using NEREA.Core.Models;
using NEREA.Core.Utilities;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NEREA.App.Services;

/// <summary>
/// System Media Transport Controls: media keys, lock-screen / flyout controls and "now playing" metadata.
/// </summary>
public sealed class SmtcService
{
    private SystemMediaTransportControls? smtc;

    /// <summary>Raised on a background thread; marshal to the UI thread before use.</summary>
    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    public void Initialize(IntPtr hwnd)
    {
        try
        {
            smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.IsStopEnabled = false;
            smtc.ButtonPressed += (_, e) => ButtonPressed?.Invoke(e.Button);
        }
        catch (Exception ex)
        {
            Log.Warn("System media controls are unavailable.", ex);
            smtc = null;
        }
    }

    public void SetStatus(bool hasTrack, bool playing)
    {
        if (smtc == null) return;
        try
        {
            smtc.PlaybackStatus = !hasTrack ? MediaPlaybackStatus.Stopped
                : playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch { /* cosmetic */ }
    }

    public async void UpdateTrack(Track? track)
    {
        if (smtc == null) return;
        try
        {
            var updater = smtc.DisplayUpdater;
            if (track == null) { updater.ClearAll(); updater.Update(); return; }

            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = track.Title;
            updater.MusicProperties.Artist = track.Artist;
            updater.MusicProperties.AlbumTitle = track.Album;
            updater.Thumbnail = null;
            if (!string.IsNullOrEmpty(track.ArtPath) && File.Exists(track.ArtPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(track.ArtPath);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            }
            updater.Update();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update system media controls.", ex);
        }
    }
}

using NEREA.Core.Models;
using NEREA.Core.Persistence;

namespace NEREA.App.Services;

/// <summary>Owns the persisted <see cref="AppSettings"/>. Saves are debounced; <see cref="Changed"/> fires immediately.</summary>
public sealed class SettingsService : IDisposable
{
    private readonly JsonFileStore<AppSettings> store = new(AppPaths.SettingsFile);
    private readonly Timer saveTimer;

    public AppSettings Current { get; }

    /// <summary>Raised on the calling thread after <see cref="NotifyChanged"/>.</summary>
    public event Action? Changed;

    public SettingsService()
    {
        Current = store.Load();
        Current.Volume = Math.Clamp(Current.Volume, 0, 1);
        Current.UiScale = Math.Clamp(Current.UiScale, 0.85, 1.5);
        saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void NotifyChanged()
    {
        Changed?.Invoke();
        saveTimer.Change(600, Timeout.Infinite);
    }

    /// <summary>Persists without raising <see cref="Changed"/> (used for state like playback position).</summary>
    public void SaveSoon() => saveTimer.Change(600, Timeout.Infinite);

    public void SaveNow() => store.Save(Current);

    public void Dispose()
    {
        saveTimer.Dispose();
        SaveNow();
    }
}

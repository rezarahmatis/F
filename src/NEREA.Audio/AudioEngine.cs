using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Utilities;

namespace NEREA.Audio;

public enum EnginePlaybackState { Stopped, Playing, Paused }

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Local music playback on WASAPI (shared mode). Completely independent of the graphics loop:
/// the only coupling is a lock-free tap that hands samples to <see cref="SpectrumAnalyzer"/>.
/// Every failure (missing file, corrupt data, vanished device) is reported through <see cref="Error"/>
/// with a human readable message and never throws to the caller.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly SpectrumAnalyzer analyzer;
    private readonly object gate = new();

    private WaveStream? reader;
    private WasapiOut? output;
    private VolumeSampleProvider? volumeStage;
    private string? currentPath;
    private double replayGainDb = double.NaN;
    private bool manualStop;
    private bool disposed;

    private double volume = 0.7;
    private bool muted;
    private bool normalize = true;
    private string? deviceId;

    public AudioEngine(SpectrumAnalyzer analyzer) => this.analyzer = analyzer;

    public event Action<EnginePlaybackState>? StateChanged;
    /// <summary>The current track played to its end.</summary>
    public event Action? TrackEnded;
    public event Action<string>? Error;

    public EnginePlaybackState State { get; private set; } = EnginePlaybackState.Stopped;
    public bool HasTrack => reader != null;
    public string? CurrentPath => currentPath;

    public TimeSpan Duration { get { lock (gate) return reader?.TotalTime ?? TimeSpan.Zero; } }

    public TimeSpan Position
    {
        get { lock (gate) { try { return reader?.CurrentTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } } }
    }

    public double Volume
    {
        get => volume;
        set { volume = Math.Clamp(value, 0, 1); ApplyVolume(); }
    }

    public bool Muted
    {
        get => muted;
        set { muted = value; ApplyVolume(); }
    }

    public bool NormalizeVolume
    {
        get => normalize;
        set { normalize = value; ApplyVolume(); }
    }

    public string? DeviceId => deviceId;

    // ───────────────────────────── devices ─────────────────────────────

    public static IReadOnlyList<AudioDevice> EnumerateDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try { using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = def.ID; }
            catch { /* no default device */ }

            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDevice(d.ID, d.FriendlyName, d.ID == defaultId));
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Audio devices could not be listed.", ex);
        }
        return list;
    }

    /// <summary>Selects the output device (null = system default). Playback continues on the new device.</summary>
    public void SetDevice(string? id)
    {
        if (id == deviceId) return;
        deviceId = id;
        lock (gate)
        {
            if (reader == null) return;
            bool wasPlaying = State == EnginePlaybackState.Playing;
            RebuildOutput(wasPlaying);
        }
    }

    // ───────────────────────────── transport ─────────────────────────────

    /// <summary>Opens a file ready to play. Returns false (and raises <see cref="Error"/>) when it cannot be played.</summary>
    public bool Load(string path, double startSeconds = 0, double gainDb = double.NaN)
    {
        lock (gate)
        {
            if (disposed) return false;
            CloseCurrent();

            if (!File.Exists(path))
            {
                Error?.Invoke($"“{Path.GetFileName(path)}” could not be found. It may have been moved or deleted.");
                return false;
            }

            try
            {
                reader = OpenReader(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not decode '{path}'.", ex);
                Error?.Invoke($"“{Path.GetFileName(path)}” could not be played. The file may be damaged or in an unsupported format.");
                return false;
            }

            currentPath = path;
            replayGainDb = gainDb;
            analyzer.Reset();

            if (!BuildOutput())
            {
                CloseCurrent();
                return false;
            }

            if (startSeconds > 0.5 && startSeconds < reader.TotalTime.TotalSeconds - 1)
            {
                try { reader.CurrentTime = TimeSpan.FromSeconds(startSeconds); } catch { /* start from the beginning */ }
            }
            SetState(EnginePlaybackState.Paused);
            return true;
        }
    }

    public void Play()
    {
        lock (gate)
        {
            if (output == null || reader == null) return;
            try
            {
                output.Play();
                SetState(EnginePlaybackState.Playing);
            }
            catch (Exception ex)
            {
                Log.Error("Playback could not start.", ex);
                Error?.Invoke("Playback could not start on the selected audio device.");
                RebuildOutput(true);
            }
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (output == null) return;
            try { output.Pause(); } catch (Exception ex) { Log.Warn("Pause failed.", ex); }
            SetState(EnginePlaybackState.Paused);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            CloseCurrent();
            SetState(EnginePlaybackState.Stopped);
        }
    }

    public void Seek(double seconds)
    {
        lock (gate)
        {
            if (reader == null) return;
            try
            {
                var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.2)));
                reader.CurrentTime = target;
                analyzer.Reset();
            }
            catch (Exception ex)
            {
                Log.Warn("Seek failed.", ex);
            }
        }
    }

    // ───────────────────────────── internals ─────────────────────────────

    private static WaveStream OpenReader(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".ogg" or ".oga")
            return new VorbisWaveReader(path);

        try
        {
            return new AudioFileReader(path);
        }
        catch (Exception first)
        {
            Log.Warn($"Primary decoder failed for '{path}', trying Media Foundation.", first);
            return new MediaFoundationReader(path);
        }
    }

    private bool BuildOutput()
    {
        if (reader == null) return false;

        ISampleProvider source = reader as ISampleProvider ?? reader.ToSampleProvider();
        var tap = new TapSampleProvider(source, analyzer);
        volumeStage = new VolumeSampleProvider(tap);
        ApplyVolume();

        IWavePlayer? created = null;
        string? failedDevice = null;
        try
        {
            created = CreateOutput(deviceId);
        }
        catch (Exception ex)
        {
            failedDevice = deviceId;
            Log.Warn("Selected output device unavailable; using the default device.", ex);
            try { created = CreateOutput(null); }
            catch (Exception ex2)
            {
                Log.Error("No audio output device available.", ex2);
                Error?.Invoke("No audio output device is available. Connect speakers or headphones and try again.");
                return false;
            }
        }

        if (failedDevice != null)
        {
            deviceId = null;
            Error?.Invoke("The selected audio device is unavailable. NEREA switched to the default device.");
        }

        output = (WasapiOut)created!;
        try
        {
            output.Init(new SampleToWaveProvider(volumeStage));
        }
        catch (Exception ex)
        {
            Log.Error("Audio output initialisation failed.", ex);
            Error?.Invoke("The audio device could not be initialised for this file.");
            output.Dispose();
            output = null;
            return false;
        }
        output.PlaybackStopped += OnPlaybackStopped;
        return true;
    }

    private static IWavePlayer CreateOutput(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = id != null
            ? enumerator.GetDevice(id)
            : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
    }

    private void RebuildOutput(bool resume)
    {
        if (reader == null) return;
        var pos = reader.CurrentTime;
        DisposeOutput();
        if (!BuildOutput()) { CloseCurrent(); SetState(EnginePlaybackState.Stopped); return; }
        try { reader.CurrentTime = pos; } catch { /* ignore */ }
        if (resume) Play(); else SetState(EnginePlaybackState.Paused);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        bool ended;
        lock (gate)
        {
            if (manualStop || sender != output) return;
            ended = reader != null && reader.CurrentTime >= reader.TotalTime - TimeSpan.FromMilliseconds(400);
            if (e.Exception != null && !ended)
            {
                // Typically the output device disappeared (headphones unplugged). Move to the default device.
                Log.Warn("Playback stopped unexpectedly.", e.Exception);
                Error?.Invoke("The audio device was disconnected. Playback moved to the default device.");
                deviceId = null;
                RebuildOutput(true);
                return;
            }
        }
        if (ended)
        {
            SetState(EnginePlaybackState.Stopped);
            TrackEnded?.Invoke();
        }
    }

    private void ApplyVolume()
    {
        var stage = volumeStage;
        if (stage == null) return;
        double gain = 1.0;
        if (normalize && !double.IsNaN(replayGainDb))
            gain = Math.Clamp(Math.Pow(10.0, replayGainDb / 20.0), 0.3, 1.0); // never boost: avoids clipping
        double perceptual = volume * volume; // gentler low end than a linear slider
        stage.Volume = muted ? 0f : (float)(perceptual * gain);
    }

    private void SetState(EnginePlaybackState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void DisposeOutput()
    {
        manualStop = true;
        try
        {
            if (output != null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                try { output.Stop(); } catch { /* already stopped */ }
                output.Dispose();
            }
        }
        catch (Exception ex) { Log.Warn("Output disposal failed.", ex); }
        output = null;
        manualStop = false;
    }

    private void CloseCurrent()
    {
        DisposeOutput();
        try { reader?.Dispose(); } catch { /* ignore */ }
        reader = null;
        volumeStage = null;
        currentPath = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            CloseCurrent();
        }
    }

    /// <summary>Passes audio through unchanged while handing a copy to the spectrum analyzer.</summary>
    private sealed class TapSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider inner;
        private readonly SpectrumAnalyzer analyzer;

        public TapSampleProvider(ISampleProvider inner, SpectrumAnalyzer analyzer)
        {
            this.inner = inner;
            this.analyzer = analyzer;
        }

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (read > 0)
                analyzer.Push(buffer.AsSpan(offset, read), inner.WaveFormat.Channels, inner.WaveFormat.SampleRate);
            return read;
        }
    }
}

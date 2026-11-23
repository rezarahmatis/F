using System.Diagnostics;

namespace NEREA.Core.Engine.AudioAnalysis;

/// <summary>
/// Turns the live audio stream into <see cref="MusicState"/>.
/// <see cref="Push"/> is called from the audio thread; <see cref="Analyze"/> from the simulation thread.
/// The two are decoupled by a ring buffer so rendering can never stall playback.
/// </summary>
public sealed class SpectrumAnalyzer
{
    private const int FftSize = 2048;
    private const int RingSize = 16384; // power of two

    private readonly float[] ring = new float[RingSize];
    private long writeIndex;                  // total samples written (monotonic)
    private long lastPushTicks;
    private volatile int sampleRate = 44100;

    private readonly Fft fft = new(FftSize);
    private readonly float[] window = new float[FftSize];
    private readonly float[] re = new float[FftSize];
    private readonly float[] im = new float[FftSize];
    private readonly float[] prevMag = new float[FftSize / 2];

    // Smoothed outputs
    private float bass, mid, treble, energy, beat, activity;
    // Slow descriptors (several seconds) - decide the overall "atmosphere" of a song
    private float slowEnergy, slowCentroid = 0.5f;
    // Automatic gain: quiet tracks still move the world, loud tracks do not overwhelm it
    private float peakBass = 0.05f, peakMid = 0.05f, peakTreble = 0.05f;
    private float bassAverage;

    public MusicState Current { get; private set; } = MusicState.Silent;

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            window[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FftSize - 1));
    }

    /// <summary>Feeds interleaved float samples. Safe to call from the audio thread.</summary>
    public void Push(ReadOnlySpan<float> interleaved, int channels, int rate)
    {
        if (channels <= 0 || interleaved.IsEmpty) return;
        sampleRate = rate;
        long w = writeIndex;
        for (int i = 0; i + channels <= interleaved.Length; i += channels)
        {
            float s = 0f;
            for (int c = 0; c < channels; c++) s += interleaved[i + c];
            ring[w & (RingSize - 1)] = s / channels;
            w++;
        }
        writeIndex = w;
        Volatile.Write(ref lastPushTicks, Stopwatch.GetTimestamp());
    }

    /// <summary>Clears history (e.g. on track change).</summary>
    public void Reset()
    {
        Array.Clear(ring);
        Array.Clear(prevMag);
    }

    /// <summary>Computes the next smoothed state. Call once per simulation step.</summary>
    public MusicState Analyze(float dt, bool playing)
    {
        dt = Math.Clamp(dt, 0.001f, 0.25f);
        double sincePush = (Stopwatch.GetTimestamp() - Volatile.Read(ref lastPushTicks)) / (double)Stopwatch.Frequency;
        bool live = playing && sincePush < 0.25;

        float rawBass = 0, rawMid = 0, rawTreble = 0, flux = 0, centroid = slowCentroid;

        if (live)
        {
            long end = writeIndex;
            long start = end - FftSize;
            for (int i = 0; i < FftSize; i++)
            {
                long idx = start + i;
                re[i] = idx < 0 ? 0f : ring[idx & (RingSize - 1)] * window[i];
                im[i] = 0f;
            }
            fft.Transform(re, im);

            float binHz = sampleRate / (float)FftSize;
            double sumB = 0, sumM = 0, sumT = 0, sumAll = 0, weighted = 0, fluxSum = 0;
            int half = FftSize / 2;
            for (int k = 1; k < half; k++)
            {
                float mag = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]) / FftSize;
                float hz = k * binHz;
                float p = mag * mag;
                if (hz < 150f) sumB += p;
                else if (hz < 2000f) sumM += p;
                else if (hz < 12000f) sumT += p;
                sumAll += p;
                weighted += p * MathF.Min(hz, 12000f);
                float d = mag - prevMag[k];
                if (d > 0) fluxSum += d;
                prevMag[k] = mag;
            }

            rawBass = MathF.Sqrt((float)sumB) * 6f;
            rawMid = MathF.Sqrt((float)sumM) * 6f;
            rawTreble = MathF.Sqrt((float)sumT) * 9f;
            flux = (float)fluxSum * 40f;
            centroid = sumAll > 1e-12 ? Math.Clamp((float)(weighted / sumAll) / 5000f, 0f, 1f) : slowCentroid;
        }

        // Automatic gain control (fast attack, very slow release)
        peakBass = Agc(peakBass, rawBass, dt);
        peakMid = Agc(peakMid, rawMid, dt);
        peakTreble = Agc(peakTreble, rawTreble, dt);

        float nBass = Math.Clamp(rawBass / peakBass, 0f, 1f);
        float nMid = Math.Clamp(rawMid / peakMid, 0f, 1f);
        float nTreble = Math.Clamp(rawTreble / peakTreble, 0f, 1f);

        // Organic smoothing: quick-ish attack, slow release
        bass = Smooth(bass, nBass, dt, 0.12f, 0.55f);
        mid = Smooth(mid, nMid, dt, 0.14f, 0.6f);
        treble = Smooth(treble, nTreble, dt, 0.08f, 0.45f);
        float nEnergy = Math.Clamp(0.5f * nBass + 0.3f * nMid + 0.2f * nTreble, 0f, 1f);
        energy = Smooth(energy, nEnergy, dt, 0.25f, 0.9f);
        activity = Smooth(activity, Math.Clamp(flux, 0f, 1f), dt, 0.3f, 1.2f);

        // Beat: bass rising above its own recent average, decayed softly (never a hard flash)
        bassAverage += (nBass - bassAverage) * Math.Min(1f, dt * 1.6f);
        float onset = MathF.Max(0f, nBass - bassAverage * 1.25f - 0.08f);
        beat = MathF.Max(beat * MathF.Exp(-dt / 0.38f), Math.Clamp(onset * 2.2f, 0f, 1f));

        // Slow descriptors: tau about 9 s. Calm piano -> high Calm, low centroid. Driving electronic -> the opposite.
        if (live)
        {
            slowEnergy = Smooth(slowEnergy, nEnergy * 0.6f + Math.Clamp(flux, 0f, 1f) * 0.4f, dt, 9f, 9f);
            slowCentroid = Smooth(slowCentroid, centroid, dt, 9f, 9f);
        }
        else
        {
            slowEnergy = Smooth(slowEnergy, 0f, dt, 4f, 4f);
        }

        float calm = 1f - Math.Clamp((slowEnergy - 0.15f) / 0.6f, 0f, 1f);
        Current = new MusicState(bass, mid, treble, energy, beat, activity, calm, slowCentroid, live);
        return Current;
    }

    private static float Agc(float peak, float value, float dt)
    {
        if (value > peak) return value;
        // release over ~20 s, never below a floor so silence is not amplified
        return MathF.Max(0.04f, peak - peak * dt * 0.05f);
    }

    private static float Smooth(float current, float target, float dt, float attackTau, float releaseTau)
    {
        float tau = target > current ? attackTau : releaseTau;
        return current + (target - current) * (1f - MathF.Exp(-dt / tau));
    }
}

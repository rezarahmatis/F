using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using NEREA.Core.Content;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Engine.FishSimulation;
using NEREA.Core.Engine.Quality;
using NEREA.Core.Models;
using NEREA.Core.Utilities;

namespace NEREA.Rendering;

public enum PointerClickKind { Primary, Secondary, Double }

/// <summary>
/// Runs the living world on its own thread: music analysis, ecosystem simulation and rendering.
/// It never touches the audio engine's threads, so playback cannot be interrupted by anything that happens here
/// (world changes, settings, minimising, cinematic mode ...). The UI talks to it through thread-safe calls.
/// </summary>
public sealed class RenderHost : IDisposable
{
    private readonly ContentCatalog catalog;
    private readonly SpectrumAnalyzer analyzer;
    private readonly Func<bool> isPlaying;

    private AquaticRenderer? renderer;
    private Thread? thread;
    private volatile bool stopRequested;
    private volatile bool paused;
    private readonly ConcurrentQueue<Action> commands = new();

    // State owned by the render thread
    private readonly SceneCamera camera = new();
    private readonly EcosystemSimulation simulation;
    private readonly RenderFrame frame = new();
    private readonly AdaptiveQualityController adaptive = new();
    private AppSettings settings = new();
    private PresentationMode mode = PresentationMode.Normal;
    private AtmosphereMood mood = AtmosphereMood.Calm;
    private bool batterySaver;
    private bool qualityDirty = true;
    private RenderQuality quality;
    private WorldDefinition? world;
    private PendingWorld? pendingWorld;
    private float fade;
    private bool fadingOut;
    private float simTime;
    private bool firstFrameSent;
    private bool preferLowPower;

    // Pointer state shared with the UI thread
    private readonly object pointerLock = new();
    private bool pointerPresent;
    private bool pointerPressed;
    private float pointerX = 0.5f, pointerY = 0.5f;
    private readonly ConcurrentQueue<(float X, float Y, PointerClickKind Kind)> clicks = new();
    private Vector2 smoothNdc;
    private float lastPx = 0.5f, lastPy = 0.5f;
    private float pointerSpeed;
    private float rippleTravel;
    private float lastRippleTime = -10f;
    private int rippleHead;

    private sealed record PendingWorld(WorldDefinition World, EcosystemConfig Config);

    /// <summary>Raised (on the render thread) after the first frame has been presented.</summary>
    public event Action? FirstFrameRendered;
    /// <summary>Raised when rendering had to stop. The message is human readable.</summary>
    public event Action<string>? Failed;
    /// <summary>Raised after a lost device was recovered: the UI must re-attach <see cref="SwapChainPointer"/>.</summary>
    public event Action? SwapChainReplaced;

    public string AdapterName => renderer?.AdapterName ?? "";
    public bool IsSoftwareAdapter => renderer?.IsSoftwareAdapter ?? false;
    public IntPtr SwapChainPointer => renderer?.SwapChainPointer ?? IntPtr.Zero;
    public RenderQuality CurrentQuality => quality;

    public RenderHost(ContentCatalog catalog, SpectrumAnalyzer analyzer, Func<bool> isPlaying)
    {
        this.catalog = catalog;
        this.analyzer = analyzer;
        this.isPlaying = isPlaying;
        simulation = new EcosystemSimulation(camera);
        frame.Camera = camera;
    }

    /// <summary>Creates the GPU renderer. Returns false (with a message) when DirectX 12 is unavailable.</summary>
    public bool TryInitialize(bool lowPowerGpu, out string? error)
    {
        preferLowPower = lowPowerGpu;
        try
        {
            renderer = new AquaticRenderer(lowPowerGpu);
            error = null;
            return true;
        }
        catch (GraphicsUnavailableException ex)
        {
            Log.Error("Renderer unavailable.", ex);
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Renderer initialisation failed.", ex);
            error = "The graphics engine could not start on this PC. The ocean is shown in simplified form; music keeps playing.";
            return false;
        }
    }

    public void Start()
    {
        if (thread != null || renderer == null) return;
        thread = new Thread(Loop) { Name = "NEREA Render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    // ───────────────────────────── thread-safe API ─────────────────────────────

    public void Resize(int pixelWidth, int pixelHeight, float scaleX, float scaleY)
    {
        renderer?.Resize(pixelWidth, pixelHeight, scaleX, scaleY);
        commands.Enqueue(() => camera.SetAspect(pixelWidth / (float)Math.Max(1, pixelHeight)));
    }

    public void SetWorld(WorldDefinition newWorld, EcosystemConfig config) =>
        commands.Enqueue(() => pendingWorld = new PendingWorld(newWorld, config.Clone()));

    /// <summary>Re-applies the current world with a new species configuration (editing the aquarium).</summary>
    public void SetEcosystem(EcosystemConfig config) =>
        commands.Enqueue(() =>
        {
            if (world == null) return;
            simulation.Configure(world, config.Species, catalog, (float)settings.FishPopulation);
            renderer?.LoadWorld(world, simulation.ActiveSpecies, PlantDensity());
        });

    public void SetSettings(AppSettings newSettings) =>
        commands.Enqueue(() => { settings = newSettings.Clone(); qualityDirty = true; });

    public void SetPresentation(PresentationMode newMode) =>
        commands.Enqueue(() => { mode = newMode; qualityDirty = true; });

    public void SetMood(AtmosphereMood newMood) => commands.Enqueue(() => mood = newMood);

    public void SetBatterySaver(bool on) => commands.Enqueue(() => { batterySaver = on; qualityDirty = true; });

    /// <summary>Pauses rendering (window minimised / hidden). Audio is unaffected.</summary>
    public void SetPaused(bool value) => paused = value;

    public void PointerMoved(float nx, float ny, bool pressed)
    {
        lock (pointerLock)
        {
            pointerPresent = true;
            pointerX = Math.Clamp(nx, 0f, 1f);
            pointerY = Math.Clamp(ny, 0f, 1f);
            pointerPressed = pressed;
        }
    }

    public void PointerLeft()
    {
        lock (pointerLock) { pointerPresent = false; pointerPressed = false; }
    }

    public void PointerClicked(float nx, float ny, PointerClickKind kind) => clicks.Enqueue((nx, ny, kind));

    // ───────────────────────────── render thread ─────────────────────────────

    private float PlantDensity() =>
        settings.Performance is PerformanceMode.Performance or PerformanceMode.Battery ? 0.6f : 1f;

    private void Loop()
    {
        if (OperatingSystem.IsWindows()) NativeTimer.BeginPeriod(1);
        var clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds;

        try
        {
            while (!stopRequested)
            {
                while (commands.TryDequeue(out var command))
                {
                    try { command(); }
                    catch (Exception ex) { Log.Error("Render command failed.", ex); }
                }

                if (paused)
                {
                    Thread.Sleep(60);
                    last = clock.Elapsed.TotalSeconds;
                    continue;
                }

                if (renderer == null || renderer.IsLost)
                {
                    if (!RecoverDevice()) return;
                    last = clock.Elapsed.TotalSeconds;
                    continue;
                }

                double frameStart = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(frameStart - last, 0.1);
                last = frameStart;

                Step(dt);

                if (!firstFrameSent && world != null && fade > 0.05f)
                {
                    firstFrameSent = true;
                    FirstFrameRendered?.Invoke();
                }

                // Frame pacing: Present(1) provides vsync, the limiter provides the FPS cap (ambient 30, battery 30 ...)
                double target = 1.0 / Math.Max(15, quality.MaxFps);
                double elapsed;
                while ((elapsed = clock.Elapsed.TotalSeconds - frameStart) < target - 0.0015)
                    Thread.Sleep(1);
                while (clock.Elapsed.TotalSeconds - frameStart < target) Thread.SpinWait(50);

                if (adaptive.Observe((float)(clock.Elapsed.TotalSeconds - frameStart), quality.MaxFps))
                    qualityDirty = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Render loop crashed.", ex);
            Failed?.Invoke("The graphics engine stopped unexpectedly. Music keeps playing.");
        }
        finally
        {
            if (OperatingSystem.IsWindows()) NativeTimer.EndPeriod(1);
        }
    }

    private bool RecoverDevice()
    {
        Log.Warn("Recovering from graphics device loss.");
        try
        {
            renderer?.Dispose();
            renderer = new AquaticRenderer(preferLowPower);
            if (world != null)
            {
                renderer.LoadWorld(world, simulation.ActiveSpecies, PlantDensity());
            }
            SwapChainReplaced?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Graphics device could not be recovered.", ex);
            renderer = null;
            Failed?.Invoke("The graphics device was lost and could not be restored. Music keeps playing.");
            return false;
        }
    }

    private void Step(float dt)
    {
        if (qualityDirty)
        {
            quality = RenderQuality.Resolve(settings, mode, batterySaver, adaptive.Factor);
            qualityDirty = false;
        }

        UpdateWorldTransition(dt);
        if (world == null) return;

        simTime += dt;
        var music = analyzer.Analyze(dt, isPlaying());
        ProcessPointer(dt, simTime);

        float motion = settings.ReducedMotion ? 0.1f : 1f;
        camera.Update(simTime, smoothNdc * (float)settings.MouseSensitivity, motion);

        var inputs = new SimulationInputs
        {
            Music = music,
            Activity = MoodProfile.Activity(mood, music) * (settings.ReducedMotion ? 0.8f : 1f),
            ReactionStrength = (float)settings.FishReaction,
            DeltaTime = dt
        };
        lock (pointerLock) inputs.PointerActive = pointerPresent;
        if (inputs.PointerActive)
        {
            var (origin, dir) = camera.Ray(new Vector2(lastPx * 2f - 1f, 1f - lastPy * 2f));
            inputs.PointerOrigin = origin;
            inputs.PointerDirection = dir;
            inputs.PointerSpeed = pointerSpeed;
        }
        simulation.Update(dt, inputs);

        frame.Time = simTime;
        frame.World = world;
        frame.Music = music;
        frame.Quality = quality;
        frame.Fade = SmoothStep(fade);
        frame.LightScale = MoodProfile.LightScale(mood);
        frame.ReducedMotion = settings.ReducedMotion;

        renderer!.Render(frame, simulation.Agents, simulation.ActiveSpecies);
    }

    private void UpdateWorldTransition(float dt)
    {
        if (pendingWorld != null && !fadingOut)
        {
            if (world == null) ApplyPending(); // first world: appear right away, fade in from black
            else fadingOut = true;
        }

        if (fadingOut)
        {
            fade -= dt / 0.45f;
            if (fade <= 0f)
            {
                fade = 0f;
                fadingOut = false;
                ApplyPending();
            }
        }
        else if (fade < 1f)
        {
            fade = MathF.Min(1f, fade + dt / (world != null && firstFrameSent ? 1.3f : 1.8f));
        }
    }

    private void ApplyPending()
    {
        var pw = pendingWorld;
        pendingWorld = null;
        if (pw == null || renderer == null) return;

        world = pw.World;
        simulation.Configure(pw.World, pw.Config.Species, catalog, (float)settings.FishPopulation);
        renderer.LoadWorld(pw.World, simulation.ActiveSpecies, PlantDensity());
        frame.RippleCount = 0;
        Array.Clear(frame.Ripples);
        rippleHead = 0;
        adaptive.Reset();
        qualityDirty = true;
    }

    private void ProcessPointer(float dt, float now)
    {
        float px, py; bool present, pressed;
        lock (pointerLock) { px = pointerX; py = pointerY; present = pointerPresent; pressed = pointerPressed; }

        Vector2 targetNdc = present ? new Vector2(px * 2f - 1f, 1f - py * 2f) : Vector2.Zero;
        smoothNdc = Vector2.Lerp(smoothNdc, targetNdc, 1f - MathF.Exp(-dt * 3f));

        float sens = (float)settings.MouseSensitivity;
        float ripple = (float)settings.RippleStrength;

        if (present)
        {
            float dx = (px - lastPx) * camera.Aspect;
            float dy = py - lastPy;
            float travel = MathF.Sqrt(dx * dx + dy * dy);
            float speedNorm = Math.Clamp(travel / Math.Max(dt, 1e-3f) / 2.2f * sens, 0f, 1f);
            pointerSpeed += (speedNorm - pointerSpeed) * (1f - MathF.Exp(-dt * 10f));
            rippleTravel += travel;

            float threshold = pressed ? 0.02f : 0.045f;
            float cooldown = pressed ? 0.05f : 0.1f;
            if (rippleTravel > threshold && now - lastRippleTime > cooldown && ripple > 0.01f)
            {
                float strength = (0.28f + 0.55f * pointerSpeed) * ripple * (pressed ? 2.0f : 1f);
                AddRipple(px, py, now, strength);
                rippleTravel = 0f;
                lastRippleTime = now;
            }
            lastPx = px; lastPy = py;
        }
        else
        {
            pointerSpeed *= MathF.Exp(-dt * 4f);
        }

        while (clicks.TryDequeue(out var click))
        {
            float strength = click.Kind switch { PointerClickKind.Secondary => 2.2f, PointerClickKind.Double => 1.9f, _ => 1.5f };
            AddRipple(click.X, click.Y, now, strength * ripple);
            var (origin, dir) = camera.Ray(new Vector2(click.X * 2f - 1f, 1f - click.Y * 2f));
            simulation.Scare(origin, dir, click.Kind == PointerClickKind.Secondary ? 1.6f : 1.0f);
        }
    }

    private void AddRipple(float x, float y, float time, float strength)
    {
        frame.Ripples[rippleHead] = new Vector4(x, y, time, strength);
        rippleHead = (rippleHead + 1) % RenderFrame.MaxRipples;
        frame.RippleCount = Math.Min(RenderFrame.MaxRipples, frame.RippleCount + 1);
    }

    private static float SmoothStep(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }

    public void Dispose()
    {
        stopRequested = true;
        thread?.Join(2000);
        thread = null;
        renderer?.Dispose();
        renderer = null;
    }

    private static class NativeTimer
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);
        public static void BeginPeriod(uint ms) { try { TimeBeginPeriod(ms); } catch { /* optional */ } }
        public static void EndPeriod(uint ms) { try { TimeEndPeriod(ms); } catch { /* optional */ } }
    }
}

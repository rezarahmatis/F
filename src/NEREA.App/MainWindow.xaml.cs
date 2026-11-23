using System.ComponentModel;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NEREA.App.Platform;
using NEREA.App.Services;
using NEREA.App.ViewModels;
using NEREA.Audio;
using NEREA.Core.Content;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Models;
using NEREA.Core.Utilities;
using NEREA.Rendering;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Media;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace NEREA.App;

/// <summary>
/// The shell: a full-bleed DirectX swap chain with a transparent layer of controls above it.
/// This class wires window behaviour (tray, full screen, media keys, drag-and-drop, idle fading, input)
/// to the view models and to the render host. It contains no simulation or audio logic.
/// </summary>
public sealed partial class MainWindow : Window
{
    public ShellViewModel VM { get; }

    private readonly SettingsService settings;
    private readonly EcosystemService ecosystem;
    private readonly AudioEngine engine;
    private readonly SpectrumAnalyzer analyzer;
    private readonly SmtcService smtc;
    private readonly DialogService dialogs;
    private readonly FilePickerService pickers;
    private readonly DispatcherQueue ui;
    private readonly UISettings uiSettings = new();
    private readonly AccessibilitySettings accessibility = new();

    private RenderHost? host;
    private TrayIcon? tray;
    private IntPtr hwnd;

    private readonly DispatcherQueueTimer idleTimer;
    private readonly DispatcherQueueTimer toastTimer;
    private readonly DispatcherQueueTimer worldToastTimer;
    private DateTime lastActivity = DateTime.UtcNow;
    private DateTime startedAt = DateTime.UtcNow;
    private bool pointerNearBottom;
    private bool splashHidden;
    private bool exiting;
    private bool draggingProgress;

    public MainWindow()
    {
        var sp = App.Services;
        VM = sp.GetRequiredService<ShellViewModel>();
        settings = sp.GetRequiredService<SettingsService>();
        ecosystem = sp.GetRequiredService<EcosystemService>();
        engine = sp.GetRequiredService<AudioEngine>();
        analyzer = sp.GetRequiredService<SpectrumAnalyzer>();
        smtc = sp.GetRequiredService<SmtcService>();
        dialogs = sp.GetRequiredService<DialogService>();
        pickers = sp.GetRequiredService<FilePickerService>();
        ui = sp.GetRequiredService<DispatcherQueue>();

        InitializeComponent();

        Title = "NEREA";
        LoadBrandImages();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        hwnd = WindowNative.GetWindowHandle(this);
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "NEREA.ico"));
            var bar = AppWindow.TitleBar;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;
            bar.ButtonForegroundColor = Color(0xE6, 0xEE, 0xF3);
            bar.ButtonInactiveForegroundColor = Color(0x80, 0xE6, 0xEE, 0xF3);
            bar.ButtonHoverBackgroundColor = Color(0x30, 0xFF, 0xFF, 0xFF);
            bar.ButtonHoverForegroundColor = Colors.White;
            AppWindow.Resize(new SizeInt32((int)settings.Current.WindowWidth, (int)settings.Current.WindowHeight));
        }
        catch (Exception ex) { Log.Warn("Window chrome could not be configured.", ex); }

        idleTimer = ui.CreateTimer();
        idleTimer.Interval = TimeSpan.FromMilliseconds(250);
        idleTimer.Tick += (_, _) => UpdateHud();
        toastTimer = ui.CreateTimer();
        toastTimer.IsRepeating = false;
        toastTimer.Tick += (_, _) => ToastBox.Opacity = 0;
        worldToastTimer = ui.CreateTimer();
        worldToastTimer.IsRepeating = false;
        worldToastTimer.Tick += (_, _) => WorldToast.Opacity = 0;

        // The progress slider seeks when the user lets go (not while dragging).
        ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ProgressPressed), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ProgressReleased), true);
        ProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ProgressReleased), true);

        Hud.SizeChanged += (_, _) => ApplyUiScale();
        PanelHost.SizeChanged += (_, _) => ApplyUiScale();
        Root.Loaded += OnLoaded;
        Closed += OnClosed;
        AppWindow.Closing += OnAppWindowClosing;
        AppWindow.Changed += OnAppWindowChanged;
        VisibilityChanged += OnVisibilityChanged;
    }

    private void LoadBrandImages()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding");
            BrandImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(dir, "nerea-symbol-64.png")));
            SplashSymbol.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(dir, "nerea-symbol-256.png")));
        }
        catch (Exception ex) { Log.Warn("Brand images could not be loaded.", ex); }
    }

    private static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    // ═══════════════════════════ startup ═══════════════════════════

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnLoaded;
        startedAt = DateTime.UtcNow;
        SplashIn.Begin();

        dialogs.XamlRoot = Root.XamlRoot;
        pickers.Window = this;

        // Messages from every part of the app are shown the same quiet way
        VM.Player.Notice += ShowToast;
        VM.Library.Notice += ShowToast;
        VM.Aquarium.Notice += ShowToast;
        VM.Player.TrackChanged += OnTrackChanged;
        VM.Player.PlaybackStateChanged += () => smtc.SetStatus(VM.Player.HasTrack, VM.Player.IsPlaying);
        VM.PropertyChanged += OnShellPropertyChanged;
        ecosystem.WorldChanged += OnWorldChanged;
        settings.Changed += ApplySettings;

        // Windows media keys / transport controls
        smtc.Initialize(hwnd);
        smtc.ButtonPressed += button => ui.TryEnqueue(() => OnMediaButton(button));

        // Tray
        tray = new TrayIcon(hwnd);
        tray.CommandInvoked += cmd => ui.TryEnqueue(() => OnTrayCommand(cmd));

        StartRenderer();

        ecosystem.Host = host;
        ecosystem.Initialize();
        ApplySettings();
        worldsReady();

        await VM.Library.InitializeAsync();
        VM.Aquarium.Initialize();

        if (settings.Current.Fullscreen) ToggleFullscreen();
        idleTimer.Start();
        UpdateHud();
        if (host == null) ScheduleSplashHide(1.6);
    }

    private void worldsReady() => VM.Worlds.MarkCurrent();

    private void StartRenderer()
    {
        try
        {
            host = new RenderHost(ecosystem.Catalog, analyzer, () => engine.State == EnginePlaybackState.Playing);
            bool lowPower = settings.Current.Performance == PerformanceMode.Battery;
            if (!host.TryInitialize(lowPower, out var error))
            {
                host.Dispose();
                host = null;
                ShowFallback(error ?? "The graphics engine is unavailable. Music keeps playing.");
                return;
            }

            SwapChainBinder.Attach(WorldPanel, host.SwapChainPointer);
            host.FirstFrameRendered += () => ui.TryEnqueue(() => ScheduleSplashHide(1.5));
            host.Failed += msg => ui.TryEnqueue(() => { host?.Dispose(); host = null; ecosystem.Host = null; ShowFallback(msg); });
            host.SwapChainReplaced += () => ui.TryEnqueue(() =>
            {
                try { if (host != null) { SwapChainBinder.Attach(WorldPanel, host.SwapChainPointer); UpdateRenderSize(); } }
                catch (Exception ex) { Log.Error("Swap chain re-attach failed.", ex); }
            });

            if (host.IsSoftwareAdapter)
                ShowToast("No DirectX 12 graphics card found – using software rendering. Performance mode is recommended.");

            UpdateRenderSize();
            host.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Renderer start failed.", ex);
            host?.Dispose();
            host = null;
            ShowFallback("The graphics engine could not start. Music keeps playing.");
        }
    }

    private void ShowFallback(string message)
    {
        WorldPanel.Visibility = Visibility.Collapsed;
        FallbackBackground.Visibility = Visibility.Visible;
        UpdateFallbackGradient(ecosystem.World);
        VM.GraphicsFallback = true;
        VM.GraphicsMessage = message;
        ShowToast(message);
    }

    private void UpdateFallbackGradient(WorldDefinition world)
    {
        static Windows.UI.Color C(System.Numerics.Vector3 v)
        {
            var (r, g, b) = ColorUtil.ToBytes(v);
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
        brush.GradientStops.Add(new GradientStop { Color = C(world.SurfaceColor * 0.8f), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = C(world.DeepColor), Offset = 1 });
        FallbackBackground.Background = brush;
    }

    private void ScheduleSplashHide(double minimumSeconds)
    {
        if (splashHidden) return;
        double remaining = minimumSeconds - (DateTime.UtcNow - startedAt).TotalSeconds;
        var t = ui.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromSeconds(Math.Max(0.05, remaining));
        t.Tick += (_, _) =>
        {
            if (splashHidden) return;
            splashHidden = true;
            Splash.IsHitTestVisible = false;
            Splash.Opacity = 0;
            var c = ui.CreateTimer();
            c.IsRepeating = false;
            c.Interval = TimeSpan.FromSeconds(1.3);
            c.Tick += (_, _) => Splash.Visibility = Visibility.Collapsed;
            c.Start();
        };
        t.Start();
    }

    // ═══════════════════════════ world & settings ═══════════════════════════

    private void OnWorldChanged(WorldDefinition world)
    {
        ui.TryEnqueue(() =>
        {
            if (VM.GraphicsFallback) UpdateFallbackGradient(world);
            if (!splashHidden && DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(3)) return; // startup: the splash is the intro
            WorldToast.Text = world.Name;
            WorldToast.Opacity = VM.IsCinematic ? 0 : 0.9;
            worldToastTimer.Interval = TimeSpan.FromSeconds(2.6);
            worldToastTimer.Start();
        });
    }

    private void ApplySettings()
    {
        void Apply()
        {
            var s = settings.Current;
            if (host != null)
            {
                var effective = s.Clone();
                effective.ReducedMotion = s.ReducedMotion || !uiSettings.AnimationsEnabled;
                host.SetSettings(effective);
                host.SetPresentation(VM.Mode);
                host.SetBatterySaver(IsBatterySaver());
            }

            Root.RequestedTheme = s.Theme switch { ThemePreference.Dark => ElementTheme.Dark, ThemePreference.Light => ElementTheme.Light, _ => ElementTheme.Default };
            ApplyUiScale();

            if (s.MinimizeToTray) tray?.Show("NEREA");
            else tray?.Hide();
        }
        if (ui.HasThreadAccess) Apply(); else ui.TryEnqueue(Apply);
    }

    private static bool IsBatterySaver()
    {
        try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; }
        catch { return false; }
    }

    private void ApplyUiScale()
    {
        float s = (float)Math.Clamp(settings.Current.UiScale, 0.85, 1.5);
        Hud.Scale = new Vector3(s, s, 1f);
        Hud.CenterPoint = new Vector3((float)Hud.ActualWidth / 2f, (float)Hud.ActualHeight, 0f);
        PanelHost.Scale = new Vector3(s, s, 1f);
        PanelHost.CenterPoint = new Vector3((float)PanelHost.ActualWidth, (float)PanelHost.ActualHeight / 2f, 0f);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Mode))
        {
            host?.SetPresentation(VM.Mode);
            UpdateHud();
        }
        else if (e.PropertyName == nameof(ShellViewModel.AnyPanelOpen))
        {
            UpdateHud();
        }
    }

    // ═══════════════════════════ fading HUD & cursor ═══════════════════════════

    private void NoteActivity()
    {
        lastActivity = DateTime.UtcNow;
        WindowsIntegration.SetCursorHidden(false);
    }

    private void UpdateHud()
    {
        double idle = (DateTime.UtcNow - lastActivity).TotalSeconds;
        bool highContrast = accessibility.HighContrast;
        double info, controls, tools, brand;

        if (VM.IsCinematic) { info = 0; controls = 0; tools = 0; brand = 0.3; }
        else if (VM.IsAmbient)
        {
            bool near = pointerNearBottom && idle < 3;
            info = near ? 0.9 : 0.22; controls = near ? 0.85 : 0; tools = near ? 0.85 : 0; brand = 0.45;
        }
        else
        {
            bool active = idle < 3.5 || VM.AnyPanelOpen || pointerNearBottom || highContrast;
            info = active ? 1 : 0.55; controls = active ? 1 : 0; tools = active ? 1 : 0; brand = active ? 0.85 : 0.5;
        }

        HudInfo.Opacity = info;
        HudControls.Opacity = controls;
        HudTools.Opacity = tools;
        Brand.Opacity = brand;
        HudControls.IsHitTestVisible = controls > 0.3;
        HudTools.IsHitTestVisible = tools > 0.3;

        bool hideCursor = (VM.IsCinematic || VM.IsAmbient || VM.IsFullscreen) && idle > 2.5 && !VM.AnyPanelOpen;
        WindowsIntegration.SetCursorHidden(hideCursor);
    }

    private void ShowToast(string text)
    {
        if (VM.IsCinematic || string.IsNullOrWhiteSpace(text)) return;
        ToastText.Text = text;
        ToastBox.Opacity = 1;
        toastTimer.Interval = TimeSpan.FromSeconds(3.8);
        toastTimer.Start();
    }

    // ═══════════════════════════ pointer & world interaction ═══════════════════════════

    private bool IsWorldSource(object? source) =>
        source is SwapChainPanel || ReferenceEquals(source, Root) || ReferenceEquals(source, FallbackBackground);

    private (float X, float Y) Normalized(Windows.Foundation.Point p) =>
        ((float)(p.X / Math.Max(1, WorldPanel.ActualWidth)), (float)(p.Y / Math.Max(1, WorldPanel.ActualHeight)));

    private bool IsOverPanel(PointerRoutedEventArgs e)
    {
        if (!VM.AnyPanelOpen) return false;
        var p = e.GetCurrentPoint(PanelHost).Position;
        return p.X >= 0 && p.Y >= 0 && p.X <= PanelHost.ActualWidth && p.Y <= PanelHost.ActualHeight;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        NoteActivity();
        var pt = e.GetCurrentPoint(Root);
        pointerNearBottom = pt.Position.Y > Root.ActualHeight - 170;
        if (IsOverPanel(e)) { host?.PointerLeft(); return; }

        var (nx, ny) = Normalized(e.GetCurrentPoint(WorldPanel).Position);
        host?.PointerMoved(nx, ny, pt.Properties.IsLeftButtonPressed);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NoteActivity();
        if (!IsWorldSource(e.OriginalSource) || IsOverPanel(e)) return;
        var pt = e.GetCurrentPoint(WorldPanel);
        if (pt.Properties.IsLeftButtonPressed)
        {
            var (nx, ny) = Normalized(pt.Position);
            host?.PointerClicked(nx, ny, PointerClickKind.Primary);
        }
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var (nx, ny) = Normalized(e.GetCurrentPoint(WorldPanel).Position);
        host?.PointerMoved(nx, ny, false);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        pointerNearBottom = false;
        host?.PointerLeft();
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!IsWorldSource(e.OriginalSource)) return;
        var (nx, ny) = Normalized(e.GetPosition(WorldPanel));
        host?.PointerClicked(nx, ny, PointerClickKind.Double);
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!IsWorldSource(e.OriginalSource)) return;
        var (nx, ny) = Normalized(e.GetPosition(WorldPanel));
        host?.PointerClicked(nx, ny, PointerClickKind.Secondary);
    }

    private void WorldPanel_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateRenderSize();
    private void WorldPanel_CompositionScaleChanged(SwapChainPanel sender, object args) => UpdateRenderSize();

    private void UpdateRenderSize()
    {
        if (host == null) return;
        double sx = WorldPanel.CompositionScaleX, sy = WorldPanel.CompositionScaleY;
        int pw = (int)Math.Max(64, WorldPanel.ActualWidth * sx);
        int ph = (int)Math.Max(64, WorldPanel.ActualHeight * sy);
        host.Resize(pw, ph, (float)sx, (float)sy);
    }

    // ═══════════════════════════ progress slider ═══════════════════════════

    private void ProgressPressed(object sender, PointerRoutedEventArgs e)
    {
        draggingProgress = true;
        VM.Player.UserSeeking = true;
    }

    private void ProgressReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!draggingProgress) return;
        draggingProgress = false;
        VM.Player.Seek(ProgressSlider.Value);
        VM.Player.UserSeeking = false;
    }

    private void ProgressSlider_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Left) { VM.Player.Seek(Math.Max(0, VM.Player.PositionSeconds - 5)); e.Handled = true; }
        else if (e.Key == VirtualKey.Right) { VM.Player.Seek(Math.Min(VM.Player.DurationSeconds - 0.5, VM.Player.PositionSeconds + 5)); e.Handled = true; }
    }

    // ═══════════════════════════ keyboard ═══════════════════════════

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        NoteActivity();
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        bool typing = focused is TextBox or ComboBox or PasswordBox or RichEditBox or AutoSuggestBox;
        bool ctrl = IsDown(VirtualKey.Control);

        if (e.Key == VirtualKey.Escape)
        {
            if (!VM.Escape() && VM.IsFullscreen) ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.F11) { ToggleFullscreen(); e.Handled = true; return; }

        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.O:
                    e.Handled = true;
                    var files = await pickers.PickMusicFilesAsync();
                    if (files.Count > 0) await VM.Library.ImportPathsAsync(files, playImported: !VM.Player.IsPlaying);
                    break;
                case VirtualKey.F:
                    e.Handled = true;
                    if (VM.Panel != PanelKind.Library) VM.TogglePanel(PanelKind.Library);
                    LibraryView.FocusSearch();
                    break;
                case VirtualKey.L: e.Handled = true; VM.TogglePanel(PanelKind.Library); break;
                case (VirtualKey)188: e.Handled = true; VM.TogglePanel(PanelKind.Settings); break; // Ctrl+,
            }
            return;
        }

        if (typing) return;

        switch (e.Key)
        {
            case VirtualKey.Space:
                if (focused is ButtonBase or ToggleSwitch or ListViewItem or ComboBoxItem or Slider) return; // let the control use Space
                VM.Player.PlayPause();
                e.Handled = true;
                break;
            case VirtualKey.M: VM.Player.ToggleMute(); e.Handled = true; break;
            case VirtualKey.N: VM.Player.Next(); e.Handled = true; break;
            case VirtualKey.P: VM.Player.Previous(); e.Handled = true; break;
            case VirtualKey.C: VM.ToggleCinematic(); e.Handled = true; break;
            case VirtualKey.A: VM.ToggleAmbient(); e.Handled = true; break;
        }
    }

    // ═══════════════════════════ drag & drop ═══════════════════════════

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to NEREA";
            e.DragUIOverride.IsGlyphVisible = false;
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0) await VM.Library.ImportPathsAsync(paths, playImported: !VM.Player.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Warn("Drop failed.", ex);
            ShowToast("Those items could not be added.");
        }
    }

    // ═══════════════════════════ Windows integration ═══════════════════════════

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        try
        {
            bool full = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
            AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            VM.IsFullscreen = full;
            settings.Current.Fullscreen = full;
            settings.SaveSoon();
            UpdateHud();
        }
        catch (Exception ex) { Log.Warn("Full screen toggle failed.", ex); }
    }

    private void OnMediaButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play: if (!VM.Player.IsPlaying) VM.Player.PlayPause(); break;
            case SystemMediaTransportControlsButton.Pause: if (VM.Player.IsPlaying) VM.Player.PlayPause(); break;
            case SystemMediaTransportControlsButton.Next: VM.Player.Next(); break;
            case SystemMediaTransportControlsButton.Previous: VM.Player.Previous(); break;
        }
    }

    private void OnTrackChanged(Track? track)
    {
        smtc.UpdateTrack(track);
        smtc.SetStatus(track != null, VM.Player.IsPlaying);
        Title = track != null ? $"{track.Title} · NEREA" : "NEREA";
        if (track != null && !AppWindow.IsVisible && settings.Current.ShowNotifications)
            tray?.Balloon(track.Title, track.DisplayArtist);
    }

    private void OnTrayCommand(int cmd)
    {
        switch (cmd)
        {
            case TrayIcon.CmdShow: ShowFromTray(); break;
            case TrayIcon.CmdPlayPause: VM.Player.PlayPause(); break;
            case TrayIcon.CmdNext: VM.Player.Next(); break;
            case TrayIcon.CmdPrevious: VM.Player.Previous(); break;
            case TrayIcon.CmdQuit: exiting = true; Close(); break;
        }
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        host?.SetPaused(false);
        if (!settings.Current.MinimizeToTray) tray?.Hide();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (settings.Current.MinimizeToTray && !exiting)
        {
            args.Cancel = true;
            tray?.Show("NEREA");
            AppWindow.Hide();
            host?.SetPaused(settings.Current.PauseWhenMinimized);
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange) return;
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            if (settings.Current.MinimizeToTray)
            {
                tray?.Show("NEREA");
                sender.Hide();
            }
            host?.SetPaused(settings.Current.PauseWhenMinimized);
        }
        else
        {
            host?.SetPaused(false);
        }
    }

    private void OnVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        // Rendering pauses while hidden or minimized; audio is untouched.
        host?.SetPaused(!args.Visible && settings.Current.PauseWhenMinimized);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            WindowsIntegration.SetCursorHidden(false);
            idleTimer.Stop();
            var size = AppWindow.Size;
            if (!VM.IsFullscreen && size.Width > 300 && size.Height > 200)
            {
                settings.Current.WindowWidth = size.Width;
                settings.Current.WindowHeight = size.Height;
            }
            VM.Player.Persist(force: true);
            settings.SaveNow();
            host?.Dispose();
            engine.Dispose();
            tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Shutdown cleanup failed.", ex);
        }
    }
}

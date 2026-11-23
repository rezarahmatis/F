using CommunityToolkit.Mvvm.ComponentModel;
using NEREA.App.Platform;
using NEREA.App.Services;
using NEREA.Audio;
using NEREA.Core.Models;

namespace NEREA.App.ViewModels;

/// <summary>Two-way view of <see cref="AppSettings"/>. Every change is applied immediately and persisted.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService service;
    private readonly AudioEngine engine;
    private bool loading = true;
    private readonly List<AudioDevice> devices = new();

    public IReadOnlyList<string> PerformanceNames { get; } = new[] { "Ultra", "High", "Balanced", "Performance", "Battery / Laptop" };
    public IReadOnlyList<string> FpsNames { get; } = new[] { "30 FPS", "60 FPS", "90 FPS", "120 FPS" };
    public IReadOnlyList<string> WaterNames { get; } = new[] { "Low", "Medium", "High" };
    public IReadOnlyList<string> ThemeNames { get; } = new[] { "System", "Dark", "Light" };
    public IReadOnlyList<string> DeviceNames { get; private set; } = new[] { "System default" };

    // Audio
    [ObservableProperty] private int deviceIndex;
    [ObservableProperty] private bool normalize;
    // Visual
    [ObservableProperty] private int performanceIndex;
    [ObservableProperty] private int fpsIndex;
    [ObservableProperty] private double particles;
    [ObservableProperty] private bool reflections;
    [ObservableProperty] private bool shadows;
    [ObservableProperty] private int waterIndex;
    [ObservableProperty] private double population;
    // Interaction
    [ObservableProperty] private double mouseSensitivity;
    [ObservableProperty] private double rippleStrength;
    [ObservableProperty] private double fishReaction;
    // Performance
    [ObservableProperty] private bool batteryOptimization;
    [ObservableProperty] private bool pauseWhenMinimized;
    // Application
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool minimizeToTray;
    [ObservableProperty] private bool notifications;
    [ObservableProperty] private int themeIndex;
    [ObservableProperty] private double uiScale;
    [ObservableProperty] private bool reducedMotion;

    public SettingsViewModel(SettingsService service, AudioEngine engine)
    {
        this.service = service;
        this.engine = engine;
        Load();
        loading = false;
    }

    public void RefreshDevices()
    {
        devices.Clear();
        devices.AddRange(AudioEngine.EnumerateDevices());
        DeviceNames = new[] { "System default" }.Concat(devices.Select(d => d.IsDefault ? d.Name + " (default)" : d.Name)).ToList();
        OnPropertyChanged(nameof(DeviceNames));
        var id = service.Current.OutputDeviceId;
        loading = true;
        DeviceIndex = id == null ? 0 : Math.Max(0, devices.FindIndex(d => d.Id == id) + 1);
        loading = false;
    }

    private void Load()
    {
        var s = service.Current;
        Normalize = s.NormalizeVolume;
        PerformanceIndex = (int)s.Performance;
        FpsIndex = s.FpsLimit switch { <= 30 => 0, <= 60 => 1, <= 90 => 2, _ => 3 };
        Particles = s.ParticleDensity;
        Reflections = s.Reflections;
        Shadows = s.Shadows;
        WaterIndex = Math.Clamp(s.WaterQuality, 0, 2);
        Population = s.FishPopulation;
        MouseSensitivity = s.MouseSensitivity;
        RippleStrength = s.RippleStrength;
        FishReaction = s.FishReaction;
        BatteryOptimization = s.BatteryOptimization;
        PauseWhenMinimized = s.PauseWhenMinimized;
        StartWithWindows = s.StartWithWindows;
        MinimizeToTray = s.MinimizeToTray;
        Notifications = s.ShowNotifications;
        ThemeIndex = (int)s.Theme;
        UiScale = s.UiScale;
        ReducedMotion = s.ReducedMotion;
    }

    private void Commit(Action<AppSettings> write)
    {
        if (loading) return;
        write(service.Current);
        service.NotifyChanged();
    }

    partial void OnDeviceIndexChanged(int value)
    {
        if (loading) return;
        string? id = value <= 0 || value > devices.Count ? null : devices[value - 1].Id;
        Commit(s => s.OutputDeviceId = id);
        engine.SetDevice(id);
    }

    partial void OnNormalizeChanged(bool value) { Commit(s => s.NormalizeVolume = value); engine.NormalizeVolume = value; }
    partial void OnPerformanceIndexChanged(int value) => Commit(s => s.Performance = (PerformanceMode)Math.Clamp(value, 0, 4));
    partial void OnFpsIndexChanged(int value) => Commit(s => s.FpsLimit = value switch { 0 => 30, 1 => 60, 2 => 90, _ => 120 });
    partial void OnParticlesChanged(double value) => Commit(s => s.ParticleDensity = value);
    partial void OnReflectionsChanged(bool value) => Commit(s => s.Reflections = value);
    partial void OnShadowsChanged(bool value) => Commit(s => s.Shadows = value);
    partial void OnWaterIndexChanged(int value) => Commit(s => s.WaterQuality = Math.Clamp(value, 0, 2));
    partial void OnPopulationChanged(double value) => Commit(s => s.FishPopulation = value);
    partial void OnMouseSensitivityChanged(double value) => Commit(s => s.MouseSensitivity = value);
    partial void OnRippleStrengthChanged(double value) => Commit(s => s.RippleStrength = value);
    partial void OnFishReactionChanged(double value) => Commit(s => s.FishReaction = value);
    partial void OnBatteryOptimizationChanged(bool value) => Commit(s => s.BatteryOptimization = value);
    partial void OnPauseWhenMinimizedChanged(bool value) => Commit(s => s.PauseWhenMinimized = value);
    partial void OnMinimizeToTrayChanged(bool value) => Commit(s => s.MinimizeToTray = value);
    partial void OnNotificationsChanged(bool value) => Commit(s => s.ShowNotifications = value);
    partial void OnThemeIndexChanged(int value) => Commit(s => s.Theme = (ThemePreference)Math.Clamp(value, 0, 2));
    partial void OnUiScaleChanged(double value) => Commit(s => s.UiScale = value);
    partial void OnReducedMotionChanged(bool value) => Commit(s => s.ReducedMotion = value);

    partial void OnStartWithWindowsChanged(bool value)
    {
        Commit(s => s.StartWithWindows = value);
        if (!loading) WindowsIntegration.SetStartWithWindows(value);
    }
}

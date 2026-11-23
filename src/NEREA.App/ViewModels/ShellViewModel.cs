using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEREA.App.Services;
using NEREA.Core.Models;

namespace NEREA.App.ViewModels;

public enum PanelKind { None, Library, Worlds, Aquarium, Settings }

/// <summary>Window-level state: presentation mode, which panel is open, transient messages.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly SettingsService settings;

    [ObservableProperty] private PresentationMode mode = PresentationMode.Normal;
    [ObservableProperty] private PanelKind panel = PanelKind.None;
    [ObservableProperty] private bool isFullscreen;
    [ObservableProperty] private string toastText = "";
    [ObservableProperty] private bool toastVisible;
    [ObservableProperty] private string worldToast = "";
    [ObservableProperty] private bool worldToastVisible;
    [ObservableProperty] private bool graphicsFallback;
    [ObservableProperty] private string graphicsMessage = "";

    public bool IsNormal => Mode == PresentationMode.Normal;
    public bool IsAmbient => Mode == PresentationMode.Ambient;
    public bool IsCinematic => Mode == PresentationMode.Cinematic;
    public bool LibraryOpen => Panel == PanelKind.Library;
    public bool WorldsOpen => Panel == PanelKind.Worlds;
    public bool AquariumOpen => Panel == PanelKind.Aquarium;
    public bool SettingsOpen => Panel == PanelKind.Settings;
    public bool AnyPanelOpen => Panel != PanelKind.None;

    public PlayerViewModel Player { get; }
    public LibraryViewModel Library { get; }
    public WorldsViewModel Worlds { get; }
    public AquariumViewModel Aquarium { get; }
    public SettingsViewModel Settings { get; }

    public ShellViewModel(SettingsService settings, PlayerViewModel player, LibraryViewModel library,
        WorldsViewModel worlds, AquariumViewModel aquarium, SettingsViewModel settingsVm)
    {
        this.settings = settings;
        Player = player;
        Library = library;
        Worlds = worlds;
        Aquarium = aquarium;
        Settings = settingsVm;
    }

    partial void OnModeChanged(PresentationMode value)
    {
        OnPropertyChanged(nameof(IsNormal));
        OnPropertyChanged(nameof(IsAmbient));
        OnPropertyChanged(nameof(IsCinematic));
        if (value != PresentationMode.Normal) Panel = PanelKind.None;
    }

    partial void OnPanelChanged(PanelKind value)
    {
        OnPropertyChanged(nameof(LibraryOpen));
        OnPropertyChanged(nameof(WorldsOpen));
        OnPropertyChanged(nameof(AquariumOpen));
        OnPropertyChanged(nameof(SettingsOpen));
        OnPropertyChanged(nameof(AnyPanelOpen));
        if (value == PanelKind.Settings) Settings.RefreshDevices();
        if (value == PanelKind.Aquarium) _ = Aquarium.RefreshPlaylistsAsync();
    }

    public void TogglePanel(PanelKind kind)
    {
        if (Mode != PresentationMode.Normal) Mode = PresentationMode.Normal;
        Panel = Panel == kind ? PanelKind.None : kind;
    }

    [RelayCommand] private void ToggleLibrary() => TogglePanel(PanelKind.Library);
    [RelayCommand] private void ToggleWorlds() => TogglePanel(PanelKind.Worlds);
    [RelayCommand] private void ToggleAquarium() => TogglePanel(PanelKind.Aquarium);
    [RelayCommand] private void ToggleSettings() => TogglePanel(PanelKind.Settings);
    [RelayCommand] private void ClosePanel() => Panel = PanelKind.None;

    [RelayCommand]
    public void ToggleCinematic() => Mode = Mode == PresentationMode.Cinematic ? PresentationMode.Normal : PresentationMode.Cinematic;

    [RelayCommand]
    public void ToggleAmbient() => Mode = Mode == PresentationMode.Ambient ? PresentationMode.Normal : PresentationMode.Ambient;

    /// <summary>Leaves cinematic / ambient mode, closes panels. Returns true when something changed.</summary>
    public bool Escape()
    {
        if (Mode != PresentationMode.Normal) { Mode = PresentationMode.Normal; return true; }
        if (Panel != PanelKind.None) { Panel = PanelKind.None; return true; }
        return false;
    }
}

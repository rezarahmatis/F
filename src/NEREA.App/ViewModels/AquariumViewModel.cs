using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEREA.App.Services;
using NEREA.Core.Content;
using NEREA.Core.Library;
using NEREA.Core.Models;
using NEREA.Core.Utilities;

namespace NEREA.App.ViewModels;

/// <summary>One species line in the aquarium editor with its population controls.</summary>
public sealed partial class SpeciesRow : ObservableObject
{
    private readonly EcosystemService ecosystem;

    public SpeciesDefinition Species { get; }
    public string Name => Species.Name;
    public string Category => Species.CategoryLabel;

    [ObservableProperty] private int count;
    [ObservableProperty] private bool enabled = true;

    public SpeciesRow(SpeciesDefinition species, EcosystemService ecosystem)
    {
        Species = species;
        this.ecosystem = ecosystem;
        count = ecosystem.GetCount(species.Id);
        enabled = ecosystem.IsEnabled(species.Id);
    }

    public void Refresh()
    {
        Count = ecosystem.GetCount(Species.Id);
        Enabled = ecosystem.IsEnabled(Species.Id);
    }

    [RelayCommand] private void Increase() { Count = Math.Min(40, Count + 1); ecosystem.SetCount(Species.Id, Count); }
    [RelayCommand] private void Decrease() { Count = Math.Max(0, Count - 1); ecosystem.SetCount(Species.Id, Count); }

    partial void OnEnabledChanged(bool value)
    {
        if (ecosystem.IsEnabled(Species.Id) != value) ecosystem.SetEnabled(Species.Id, value);
    }
}

/// <summary>The aquarium panel: species / quantities, atmosphere and user-saved aquariums.</summary>
public sealed partial class AquariumViewModel : ObservableObject
{
    private readonly EcosystemService ecosystem;
    private readonly AquariumService aquariums;
    private readonly LibraryDatabase db;
    private readonly LibraryViewModel library;
    private readonly PlayerViewModel player;
    private readonly DialogService dialogs;
    private List<SpeciesRow> allRows = new();

    public IReadOnlyList<string> CategoryNames { get; } = new[] { "All", "Freshwater", "Tropical reef", "Large ocean", "Deep sea", "Aquatic animals" };
    public IReadOnlyList<string> MoodNames { get; } = new[] { "Calm", "Balanced", "Lively", "Nocturnal" };

    [ObservableProperty] private IReadOnlyList<SpeciesRow> species = Array.Empty<SpeciesRow>();
    [ObservableProperty] private int categoryIndex;
    [ObservableProperty] private int moodIndex;
    [ObservableProperty] private string worldName = "";
    [ObservableProperty] private string totalText = "";
    [ObservableProperty] private bool showSaved;
    [ObservableProperty] private SavedAquarium? selectedAquarium;
    [ObservableProperty] private IReadOnlyList<Playlist> playlists = Array.Empty<Playlist>();
    [ObservableProperty] private int playlistIndex;

    public ObservableCollection<SavedAquarium> Saved { get; } = new();
    public IReadOnlyList<string> PlaylistNames => new[] { "No music" }.Concat(Playlists.Select(p => p.Name)).ToList();

    public event Action<string>? Notice;

    public AquariumViewModel(EcosystemService ecosystem, AquariumService aquariums, LibraryDatabase db,
        LibraryViewModel library, PlayerViewModel player, DialogService dialogs)
    {
        this.ecosystem = ecosystem;
        this.aquariums = aquariums;
        this.db = db;
        this.library = library;
        this.player = player;
        this.dialogs = dialogs;

        ecosystem.WorldChanged += _ => RebuildSpecies();
        ecosystem.ConfigChanged += UpdateTotal;
        ecosystem.MoodChanged += m => MoodIndex = (int)m;
        ReloadSaved();
        RebuildSpecies();
        moodIndex = (int)ecosystem.Mood;
    }

    public void Initialize() => RebuildSpecies();

    private void RebuildSpecies()
    {
        WorldName = ecosystem.World.Name;
        allRows = ecosystem.Catalog.SpeciesFor(ecosystem.World)
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .Select(s => new SpeciesRow(s, ecosystem)).ToList();
        ApplyFilter();
        MoodIndex = (int)ecosystem.Mood;
        UpdateTotal();
    }

    partial void OnCategoryIndexChanged(int value) => ApplyFilter();

    private void ApplyFilter()
    {
        Species = CategoryIndex <= 0
            ? allRows
            : allRows.Where(r => (int)r.Species.Category == CategoryIndex - 1).ToList();
    }

    private void UpdateTotal()
    {
        int n = ecosystem.Config.Species.Where(s => s.Enabled).Sum(s => s.Count);
        TotalText = n == 1 ? "1 animal" : $"{n} animals";
    }

    partial void OnMoodIndexChanged(int value)
    {
        var mood = (AtmosphereMood)Math.Clamp(value, 0, 3);
        if (mood != ecosystem.Mood) ecosystem.SetMood(mood);
    }

    [RelayCommand]
    private void ResetPopulation()
    {
        ecosystem.ResetToDefault();
        foreach (var r in allRows) r.Refresh();
        UpdateTotal();
    }

    // ───────────────────────────── saved aquariums ─────────────────────────────

    public void ReloadSaved()
    {
        Saved.Clear();
        foreach (var a in aquariums.Items) Saved.Add(a);
    }

    public async Task RefreshPlaylistsAsync()
    {
        Playlists = await Task.Run(() => db.GetPlaylists());
        OnPropertyChanged(nameof(PlaylistNames));
    }

    [RelayCommand]
    private async Task SaveNewAsync()
    {
        var name = await dialogs.PromptTextAsync("Save aquarium", "Name, for example Midnight Garden", ecosystem.World.Name, "Save");
        if (name == null) return;
        var (pid, pname) = CurrentPlaylist();
        var aq = ecosystem.Capture(name, pid, pname);
        aquariums.Add(aq);
        ReloadSaved();
        SelectedAquarium = aq;
        Notice?.Invoke($"Saved “{aq.Name}”");
    }

    [RelayCommand]
    private void UpdateSelected()
    {
        if (SelectedAquarium == null) return;
        var (pid, pname) = CurrentPlaylist();
        var updated = ecosystem.Capture(SelectedAquarium.Name, pid, pname);
        updated.Id = SelectedAquarium.Id;
        updated.Created = SelectedAquarium.Created;
        aquariums.Update(updated);
        ReloadSaved();
        SelectedAquarium = aquariums.Find(updated.Id);
        Notice?.Invoke($"Updated “{updated.Name}”");
    }

    [RelayCommand]
    private async Task RenameSelectedAsync()
    {
        if (SelectedAquarium == null) return;
        var name = await dialogs.PromptTextAsync("Rename aquarium", "Name", SelectedAquarium.Name, "Rename");
        if (name == null) return;
        var id = SelectedAquarium.Id;
        aquariums.Rename(id, name);
        ReloadSaved();
        SelectedAquarium = aquariums.Find(id);
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (SelectedAquarium == null) return;
        var copy = aquariums.Duplicate(SelectedAquarium.Id);
        ReloadSaved();
        if (copy != null) SelectedAquarium = aquariums.Find(copy.Id);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedAquarium == null) return;
        if (!await dialogs.ConfirmAsync("Delete aquarium", $"Delete “{SelectedAquarium.Name}”?")) return;
        aquariums.Delete(SelectedAquarium.Id);
        SelectedAquarium = null;
        ReloadSaved();
    }

    [RelayCommand]
    private async Task LoadSelectedAsync()
    {
        if (SelectedAquarium == null) return;
        var aq = SelectedAquarium;
        ecosystem.ApplyAquarium(aq);
        if (aq.PlaylistId is long pid)
        {
            try
            {
                var ids = await Task.Run(() => db.GetPlaylistTrackIds(pid));
                var tracks = await Task.Run(() => db.GetTracks(ids));
                if (tracks.Count > 0) player.PlayQueue(tracks, 0);
            }
            catch (Exception ex) { Log.Warn("Aquarium playlist could not be started.", ex); }
        }
        Notice?.Invoke($"“{aq.Name}”");
    }

    partial void OnSelectedAquariumChanged(SavedAquarium? value)
    {
        if (value == null) { PlaylistIndex = 0; return; }
        int i = Playlists.ToList().FindIndex(p => p.Id == value.PlaylistId);
        PlaylistIndex = i < 0 ? 0 : i + 1;
    }

    private (long?, string?) CurrentPlaylist()
    {
        if (PlaylistIndex <= 0 || PlaylistIndex > Playlists.Count) return (null, null);
        var p = Playlists[PlaylistIndex - 1];
        return (p.Id, p.Name);
    }
}

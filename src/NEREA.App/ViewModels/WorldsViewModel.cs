using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NEREA.App.Services;
using NEREA.Core.Content;

namespace NEREA.App.ViewModels;

/// <summary>A selectable world or a category header in the world list.</summary>
public sealed partial class WorldRow : ObservableObject
{
    public WorldDefinition? World { get; init; }
    public string Name { get; init; } = "";
    public bool IsHeader { get; init; }
    public bool IsWorld => !IsHeader;
    [ObservableProperty] private bool isCurrent;
}

public sealed partial class WorldsViewModel : ObservableObject
{
    private readonly EcosystemService ecosystem;
    private readonly Random rng = new();

    public IReadOnlyList<WorldRow> Rows { get; }

    public WorldsViewModel(EcosystemService ecosystem)
    {
        this.ecosystem = ecosystem;
        var rows = new List<WorldRow>();
        foreach (var group in ecosystem.Catalog.Worlds.GroupBy(w => w.Category).OrderBy(g => g.Key))
        {
            rows.Add(new WorldRow { IsHeader = true, Name = group.First().CategoryLabel.ToUpperInvariant() });
            foreach (var w in group) rows.Add(new WorldRow { World = w, Name = w.Name });
        }
        Rows = rows;
        ecosystem.WorldChanged += _ => MarkCurrent();
    }

    public void MarkCurrent()
    {
        foreach (var r in Rows) r.IsCurrent = r.World != null && r.World.Id == ecosystem.World.Id;
    }

    public void Select(WorldRow row)
    {
        if (row.World == null) return;
        ecosystem.LoadWorld(row.World.Id);
    }

    [RelayCommand]
    public void Surprise()
    {
        var worlds = ecosystem.Catalog.Worlds.Where(w => w.Id != ecosystem.World.Id).ToList();
        if (worlds.Count > 0) ecosystem.LoadWorld(worlds[rng.Next(worlds.Count)].Id);
    }
}

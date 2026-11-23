using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NEREA.Core.Models;
using NEREA.Core.Persistence;
using NEREA.Core.Utilities;

namespace NEREA.Core.Content;

/// <summary>
/// Loads the species library and the worlds. Built-in definitions are embedded in the assembly;
/// additional <c>*.json</c> files in %LOCALAPPDATA%\NEREA\Content extend or override them.
/// </summary>
public sealed class ContentCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, SpeciesDefinition> speciesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldDefinition> worldsById = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SpeciesDefinition> Species { get; private set; } = Array.Empty<SpeciesDefinition>();
    public IReadOnlyList<WorldDefinition> Worlds { get; private set; } = Array.Empty<WorldDefinition>();

    public SpeciesDefinition? FindSpecies(string id) => speciesById.GetValueOrDefault(id);
    public WorldDefinition? FindWorld(string id) => worldsById.GetValueOrDefault(id);

    public WorldDefinition DefaultWorld =>
        FindWorld("moonlit-pond") ?? Worlds.FirstOrDefault() ?? new WorldDefinition { Id = "fallback", Name = "Still Water" };

    public IEnumerable<SpeciesDefinition> SpeciesFor(WorldDefinition world) =>
        Species.Where(s => world.AllowedCategories.Contains(s.Category));

    /// <summary>Default ecosystem of a world, restricted to species that exist in the catalogue.</summary>
    public EcosystemConfig DefaultEcosystem(WorldDefinition world)
    {
        var cfg = new EcosystemConfig();
        foreach (var (id, count) in world.Fish)
            if (speciesById.ContainsKey(id)) cfg.Species.Add(new SpeciesPopulation(id, count));
        return cfg;
    }

    public static ContentCatalog Load(string? userContentDirectory = null)
    {
        var catalog = new ContentCatalog();
        var species = new List<SpeciesDefinition>();
        var worlds = new List<WorldDefinition>();

        try
        {
            species.AddRange(ReadEmbedded<List<SpeciesDefinition>>("NEREA.Content.species.json") ?? new());
            worlds.AddRange(ReadEmbedded<List<WorldDefinition>>("NEREA.Content.worlds.json") ?? new());
        }
        catch (Exception ex)
        {
            Log.Error("Built-in content could not be read.", ex);
        }

        var dir = userContentDirectory ?? AppPaths.UserContentDirectory;
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "species*.json"))
                    TryRead(file, species);
                foreach (var file in Directory.EnumerateFiles(dir, "worlds*.json"))
                    TryRead(file, worlds);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("User content directory could not be scanned.", ex);
        }

        foreach (var s in species)
        {
            if (!s.IsValid(out var why)) { Log.Warn($"Species '{s.Id}' ignored: {why}"); continue; }
            catalog.speciesById[s.Id] = s; // later definitions override earlier ones
        }
        foreach (var w in worlds)
        {
            if (!w.IsValid(out var why)) { Log.Warn($"World '{w.Id}' ignored: {why}"); continue; }
            catalog.worldsById[w.Id] = w;
        }

        catalog.Species = catalog.speciesById.Values.ToList();
        catalog.Worlds = catalog.worldsById.Values.ToList();
        return catalog;
    }

    private static void TryRead<T>(string file, List<T> into)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<T>>(File.ReadAllText(file), Options);
            if (items != null) into.AddRange(items);
        }
        catch (Exception ex)
        {
            Log.Warn($"Content file '{Path.GetFileName(file)}' is invalid and was skipped.", ex);
        }
    }

    private static T? ReadEmbedded<T>(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource {resourceName} not found.");
        return JsonSerializer.Deserialize<T>(stream, Options);
    }
}

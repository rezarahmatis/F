using NEREA.Core.Models;
using NEREA.Core.Persistence;

namespace NEREA.App.Services;

/// <summary>Custom saved aquariums: save, rename, duplicate, edit, delete, load.</summary>
public sealed class AquariumService
{
    private readonly JsonFileStore<SavedAquariumFile> store = new(AppPaths.AquariumsFile);
    private readonly SavedAquariumFile file;

    public AquariumService() => file = store.Load();

    public IReadOnlyList<SavedAquarium> Items => file.Aquariums;

    public SavedAquarium? Find(string id) => file.Aquariums.FirstOrDefault(a => a.Id == id);

    public void Add(SavedAquarium aquarium)
    {
        file.Aquariums.Add(aquarium);
        Save();
    }

    public void Update(SavedAquarium aquarium)
    {
        int i = file.Aquariums.FindIndex(a => a.Id == aquarium.Id);
        if (i >= 0) file.Aquariums[i] = aquarium;
        Save();
    }

    public void Rename(string id, string name)
    {
        var a = Find(id);
        if (a == null) return;
        a.Name = name.Trim();
        Save();
    }

    public SavedAquarium? Duplicate(string id)
    {
        var a = Find(id);
        if (a == null) return null;
        var copy = a.Duplicate(a.Name + " copy");
        file.Aquariums.Add(copy);
        Save();
        return copy;
    }

    public void Delete(string id)
    {
        file.Aquariums.RemoveAll(a => a.Id == id);
        Save();
    }

    private void Save() => store.Save(file);
}

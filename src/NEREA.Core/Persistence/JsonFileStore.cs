using System.Text.Json;
using System.Text.Json.Serialization;
using NEREA.Core.Utilities;

namespace NEREA.Core.Persistence;

/// <summary>
/// Reliable JSON persistence: atomic writes (temp file + replace) and corruption recovery
/// (a damaged file is preserved as *.bad and defaults are used instead).
/// </summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string path;
    private readonly object gate = new();

    public JsonFileStore(string path) => this.path = path;

    public T Load()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(path)) return new T();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read {Path.GetFileName(path)}; starting from defaults.", ex);
                try { File.Copy(path, path + ".bad", true); } catch { /* ignore */ }
                return new T();
            }
        }
    }

    public bool Save(T value)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save {Path.GetFileName(path)}.", ex);
                return false;
            }
        }
    }
}

using System.Reflection;
using NEREA.Core.Utilities;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace NEREA.Rendering;

/// <summary>
/// Loads HLSL from disk (<c>Assets/Shaders</c> next to the executable, for shader development) or from the
/// copy embedded in this assembly, prepends <c>Common.hlsli</c> and compiles with the D3D compiler.
/// </summary>
internal static class ShaderLibrary
{
    private static readonly Dictionary<string, byte[]> Cache = new();

    public static byte[] Compile(string file, string entry, string profile)
    {
        var key = $"{file}:{entry}:{profile}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        string source = Load("Common.hlsli") + "\n#line 1\n" + Load(file);
        var flags = ShaderFlags.OptimizationLevel3;
        var result = Compiler.Compile(source, null, null, entry, file, profile, flags, EffectFlags.None, out Blob? blob, out Blob? errors);
        if (result.Failure || blob == null)
        {
            string message = errors != null ? errors.AsString() : result.ToString();
            Log.Error($"Shader {file}:{entry} failed to compile: {message}");
            throw new InvalidOperationException($"Shader '{file}' ({entry}) failed to compile.\n{message}");
        }
        errors?.Dispose();

        var bytes = blob.AsBytes();
        blob.Dispose();
        lock (Cache) Cache[key] = bytes;
        return bytes;
    }

    private static string Load(string name)
    {
        try
        {
            var external = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", name);
            if (File.Exists(external)) return File.ReadAllText(external);
        }
        catch (Exception ex)
        {
            Log.Warn($"External shader '{name}' unreadable; using embedded copy.", ex);
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NEREA.Shaders." + name)
            ?? throw new FileNotFoundException($"Shader resource '{name}' is missing from the application.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

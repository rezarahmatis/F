using NEREA.Core.Models;
using NEREA.Core.Utilities;

namespace NEREA.Core.Library;

public readonly record struct ImportProgress(int Processed, int Total, string CurrentFile);

public sealed class ImportResult
{
    public int Added { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Problems { get; } = new();

    /// <summary>Human readable one-line summary.</summary>
    public string Summary
    {
        get
        {
            if (Cancelled) return "Import cancelled.";
            if (Added == 0 && Failed == 0 && Skipped == 0) return "No supported music files were found.";
            var parts = new List<string>();
            if (Added > 0) parts.Add($"{Added} song{(Added == 1 ? "" : "s")} added");
            if (Skipped > 0) parts.Add($"{Skipped} already in library");
            if (Failed > 0) parts.Add($"{Failed} could not be read");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Imports files and folders (recursively) into the library without blocking the caller.</summary>
public sealed class LibraryScanner
{
    private readonly LibraryDatabase db;

    public LibraryScanner(LibraryDatabase db) => this.db = db;

    public static IEnumerable<string> Enumerate(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "*.*", options); }
                catch (Exception ex) { Log.Warn($"Folder '{p}' cannot be scanned.", ex); continue; }
                foreach (var f in files)
                    if (SupportedFormats.IsSupported(f)) yield return f;
            }
            else if (File.Exists(p) && SupportedFormats.IsSupported(p))
            {
                yield return p;
            }
        }
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<string> paths, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        var result = new ImportResult();
        return await Task.Run(async () =>
        {
            var known = db.GetKnownPaths();
            var files = Enumerate(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var fresh = files.Where(f => !known.Contains(f)).ToList();
            result.Skipped = files.Count - fresh.Count;

            int processed = 0;
            var batch = new List<Track>();
            var gate = new object();

            try
            {
                await Parallel.ForEachAsync(fresh,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct },
                    (file, token) =>
                    {
                        var track = MetadataReader.Read(file);
                        lock (gate)
                        {
                            if (track == null) { result.Failed++; result.Problems.Add($"{Path.GetFileName(file)}: file could not be opened."); }
                            else
                            {
                                batch.Add(track);
                                if (batch.Count >= 200) FlushLocked(batch, result);
                            }
                            processed++;
                        }
                        if (processed % 5 == 0 || processed == fresh.Count)
                            progress?.Report(new ImportProgress(processed, fresh.Count, Path.GetFileName(file)));
                        return ValueTask.CompletedTask;
                    });
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
            }

            lock (gate) FlushLocked(batch, result);
            progress?.Report(new ImportProgress(fresh.Count, fresh.Count, ""));
            return result;
        }, CancellationToken.None);
    }

    private void FlushLocked(List<Track> batch, ImportResult result)
    {
        if (batch.Count == 0) return;
        try { result.Added += db.UpsertMany(batch); }
        catch (Exception ex)
        {
            Log.Error("Library write failed.", ex);
            result.Failed += batch.Count;
            result.Problems.Add("The library database could not be updated.");
        }
        batch.Clear();
    }
}

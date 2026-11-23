// Headless validation of everything in NEREA.Core that does not need Windows:
// content catalogue, simulation, audio analysis, persistence, library database, import pipeline.
using System.Numerics;
using NEREA.Core.Content;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Engine.FishSimulation;
using NEREA.Core.Engine.Quality;
using NEREA.Core.Library;
using NEREA.Core.Models;
using NEREA.Core.Persistence;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "  ok  " : " FAIL ")} {what}");
    if (!ok) failures++;
}

var tmp = Path.Combine(Path.GetTempPath(), "nerea-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
AppPaths.UseRoot(tmp);
AppPaths.EnsureCreated();

// ── Content ───────────────────────────────────────────────
Console.WriteLine("Content");
var catalog = ContentCatalog.Load();
Check(catalog.Species.Count >= 60, $"species loaded ({catalog.Species.Count})");
Check(catalog.Worlds.Count == 38, $"worlds loaded ({catalog.Worlds.Count})");
foreach (var w in catalog.Worlds)
{
    foreach (var id in w.Fish.Keys)
        Check(catalog.FindSpecies(id) != null, $"world '{w.Id}' references known species '{id}'");
    foreach (var id in w.Fish.Keys)
    {
        var sp = catalog.FindSpecies(id);
        if (sp != null && !w.AllowedCategories.Contains(sp.Category))
            Console.WriteLine($"  note  '{w.Id}' default contains '{id}' ({sp.Category}) outside its allowed categories (still allowed as default)");
    }
}
foreach (var name in new[] { "Koi", "Clownfish", "Anglerfish", "Jellyfish", "Whale Shark", "Seahorse", "Crab", "Pleco", "Oarfish" })
    Check(catalog.Species.Any(s => s.Name == name), $"species '{name}' present");
Check(catalog.Species.Select(s => s.Id).Distinct().Count() == catalog.Species.Count, "species ids unique");

// ── Simulation ────────────────────────────────────────────
Console.WriteLine("Simulation");
var camera = new SceneCamera();
camera.SetAspect(16f / 9f);
var sim = new EcosystemSimulation(camera);
foreach (var world in catalog.Worlds)
{
    var cfg = catalog.DefaultEcosystem(world);
    sim.Configure(world, cfg.Species, catalog, 1f);
    var inputs = new SimulationInputs
    {
        Music = MusicState.Silent, Activity = 1f, ReactionStrength = 1f,
        PointerActive = true,
        PointerOrigin = camera.Position, PointerDirection = Vector3.Normalize(new Vector3(0.1f, 0f, -1f)), PointerSpeed = 0.5f
    };
    bool finite = true;
    for (int i = 0; i < 600; i++)
    {
        if (i == 200) sim.Scare(camera.Position, Vector3.Normalize(new Vector3(0f, 0f, -1f)), 1f);
        inputs.DeltaTime = 1f / 60f;
        sim.Update(1f / 60f, inputs);
    }
    foreach (var a in sim.Agents.ToArray())
        if (!float.IsFinite(a.Position.X + a.Position.Y + a.Position.Z) || !float.IsFinite(a.Forward.X + a.Up.Y))
            finite = false;
    int expected = cfg.TotalEnabled;
    Check(finite && sim.Count > 0, $"'{world.Name}' simulates {sim.Count} animals ({expected} configured), values finite");
}

// Stay inside the world
{
    var w = catalog.FindWorld("coral-reef")!;
    sim.Configure(w, catalog.DefaultEcosystem(w).Species, catalog, 1f);
    var inp = new SimulationInputs { Music = MusicState.Silent, Activity = 1f, ReactionStrength = 1f };
    for (int i = 0; i < 1800; i++) sim.Update(1f / 60f, inp);
    bool inside = true;
    foreach (var a in sim.Agents.ToArray())
        if (a.Position.Y > w.SurfaceY + 0.5f || a.Position.Y < w.FloorY - 0.5f || a.Position.Z > 9 || a.Position.Z < -16) inside = false;
    Check(inside, "animals stay inside the water volume after 30 s");
}

// Schooling: a school should stay together
{
    var w = catalog.FindWorld("moonlit-pond")!;
    var pops = new List<SpeciesPopulation> { new("neon-tetra", 12) };
    sim.Configure(w, pops, catalog, 1f);
    var inp = new SimulationInputs { Music = MusicState.Silent, Activity = 1f, ReactionStrength = 1f };
    for (int i = 0; i < 1200; i++) sim.Update(1f / 60f, inp);
    var center = Vector3.Zero;
    foreach (var a in sim.Agents.ToArray()) center += a.Position;
    center /= sim.Count;
    float avg = 0;
    foreach (var a in sim.Agents.ToArray()) avg += Vector3.Distance(a.Position, center);
    avg /= sim.Count;
    Check(avg < 8f, $"school stays cohesive (mean distance to centre {avg:F2})");
}

// ── Audio analysis ────────────────────────────────────────
Console.WriteLine("Audio analysis");
{
    var an = new SpectrumAnalyzer();
    const int rate = 44100;
    float[] block = new float[1024 * 2];
    double phase = 0;
    MusicState bassState = default, trebleState = default;

    for (int pass = 0; pass < 2; pass++)
    {
        double freq = pass == 0 ? 80 : 6000;
        an.Reset();
        MusicState s = default;
        for (int frame = 0; frame < 120; frame++)
        {
            for (int i = 0; i < 1024; i++)
            {
                float v = (float)Math.Sin(phase) * 0.5f;
                phase += 2 * Math.PI * freq / rate;
                block[i * 2] = v; block[i * 2 + 1] = v;
            }
            an.Push(block, 2, rate);
            s = an.Analyze(1f / 43f, true);
            Thread.Sleep(1);
        }
        if (pass == 0) bassState = s; else trebleState = s;
    }
    Check(bassState.Bass > bassState.Treble, $"80 Hz tone drives bass ({bassState.Bass:F2}) over treble ({bassState.Treble:F2})");
    Check(trebleState.Treble > trebleState.Bass, $"6 kHz tone drives treble ({trebleState.Treble:F2}) over bass ({trebleState.Bass:F2})");
    var silent = an.Analyze(0.5f, false);
    for (int i = 0; i < 200; i++) silent = an.Analyze(0.05f, false);
    Check(silent.Energy < 0.05f && !silent.IsPlaying, "silence decays to rest");
}

// ── Quality ───────────────────────────────────────────────
Console.WriteLine("Quality");
{
    var s = new AppSettings();
    var normal = RenderQuality.Resolve(s, PresentationMode.Normal, false);
    var ambient = RenderQuality.Resolve(s, PresentationMode.Ambient, false);
    s.Performance = PerformanceMode.Battery;
    var battery = RenderQuality.Resolve(s, PresentationMode.Normal, false);
    Check(ambient.MaxFps <= 30 && ambient.RenderScale <= normal.RenderScale, "ambient mode reduces fps and resolution");
    Check(battery.MaxFps == 30 && !battery.Msaa, "battery preset is aggressive");
    var ctl = new AdaptiveQualityController();
    for (int i = 0; i < 400; i++) ctl.Observe(1f / 25f, 60);
    Check(ctl.Factor < 1f, $"adaptive quality lowers under load (factor {ctl.Factor:F2})");
}

// ── Persistence ───────────────────────────────────────────
Console.WriteLine("Persistence");
{
    var store = new JsonFileStore<AppSettings>(AppPaths.SettingsFile);
    var s = new AppSettings { Volume = 0.33, WorldId = "kelp-forest", Performance = PerformanceMode.High };
    s.Ecosystems["kelp-forest"] = new EcosystemConfig { Species = { new SpeciesPopulation("mackerel", 5) } };
    Check(store.Save(s), "settings saved");
    var back = store.Load();
    Check(Math.Abs(back.Volume - 0.33) < 1e-9 && back.WorldId == "kelp-forest" && back.Performance == PerformanceMode.High
          && back.Ecosystems["kelp-forest"].Species[0].Count == 5, "settings round trip");
    File.WriteAllText(AppPaths.SettingsFile, "{ this is not json");
    var recovered = store.Load();
    Check(recovered.WorldId == "moonlit-pond" && File.Exists(AppPaths.SettingsFile + ".bad"), "corrupt settings recover to defaults");

    var aq = new JsonFileStore<SavedAquariumFile>(AppPaths.AquariumsFile);
    var file = new SavedAquariumFile();
    file.Aquariums.Add(new SavedAquarium { Name = "Midnight Garden", WorldId = "moonlit-pond", Species = { new("koi", 8), new("goldfish", 5), new("common-carp", 2) } });
    aq.Save(file);
    Check(aq.Load().Aquariums[0].Duplicate("Copy").Species.Count == 3, "saved aquariums persist and duplicate");
}

// ── Library ───────────────────────────────────────────────
Console.WriteLine("Library");
{
    var db = new LibraryDatabase(AppPaths.LibraryDatabaseFile);
    db.Initialize();

    var musicDir = Path.Combine(tmp, "music", "Album A");
    Directory.CreateDirectory(musicDir);
    // Minimal valid WAV (0.1 s silence) and one deliberately corrupt "mp3"
    byte[] Wav()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataLen = 44100 / 10 * 2;
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + dataLen); bw.Write("WAVEfmt "u8.ToArray());
        bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(44100); bw.Write(44100 * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8.ToArray()); bw.Write(dataLen); bw.Write(new byte[dataLen]);
        return ms.ToArray();
    }
    File.WriteAllBytes(Path.Combine(musicDir, "01 Still Water.wav"), Wav());
    File.WriteAllBytes(Path.Combine(musicDir, "02 Ripple.wav"), Wav());
    File.WriteAllText(Path.Combine(musicDir, "03 Broken.mp3"), "not audio at all");
    File.WriteAllText(Path.Combine(musicDir, "notes.txt"), "ignored");

    var scanner = new LibraryScanner(db);
    var result = await scanner.ImportAsync(new[] { Path.Combine(tmp, "music") }, null, CancellationToken.None);
    Console.WriteLine("        " + result.Summary);
    Check(result.Added == 3, "folder import found supported files only (corrupt mp3 stays playable-by-name, txt ignored)");

    var again = await scanner.ImportAsync(new[] { Path.Combine(tmp, "music") }, null, CancellationToken.None);
    Check(again.Added == 0 && again.Skipped == 3, "re-import skips known files");

    var tracks = db.GetAllTracks();
    Check(tracks.Count == 3 && tracks.Any(t => t.Title == "01 Still Water" && t.DurationMs > 50), "metadata read (title/duration)");

    db.SetFavorite(tracks[0].Id, true);
    db.MarkPlayed(tracks[1].Id);
    Check(db.GetAllTracks().Count(t => t.IsFavorite) == 1 && db.GetAllTracks().Any(t => t.PlayCount == 1 && t.LastPlayed != null), "favourite + recently played");

    long pid = db.CreatePlaylist("Chill");
    db.AddToPlaylist(pid, tracks.Select(t => t.Id));
    db.RenamePlaylist(pid, "Chill Playlist");
    Check(db.GetPlaylists().Single().Name == "Chill Playlist" && db.GetPlaylists().Single().TrackCount == 3, "playlist create/rename/add");
    db.RemoveFromPlaylist(pid, tracks[1].Id);
    var ids = db.GetPlaylistTrackIds(pid);
    Check(ids.Count == 2 && !ids.Contains(tracks[1].Id), "playlist remove keeps order");
    db.DeletePlaylist(pid);
    Check(db.GetPlaylists().Count == 0, "playlist delete");

    // Corrupt database file is recovered
    File.WriteAllText(Path.Combine(tmp, "broken.db"), "garbage garbage garbage garbage garbage");
    var db2 = new LibraryDatabase(Path.Combine(tmp, "broken.db"));
    db2.Initialize();
    Check(db2.GetAllTracks().Count == 0, "corrupt database is rebuilt");
}

try { Directory.Delete(tmp, true); } catch { }
Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

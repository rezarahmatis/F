# NEREA

> **Let the music create the world.**

NEREA is a native Windows music player in which the music is the heartbeat of a living, realtime-rendered aquatic world. It is a **C# / .NET 8 / WinUI 3 / Windows App SDK** application with a hand-written **DirectX 12** renderer and HLSL shaders.

## Repository structure

```text
NEREA/
  Assets/                 Fish, worlds, HLSL shaders, branding and Windows icons
  src/
    NEREA.Core            Models, content, simulation, analysis, library and persistence
    NEREA.Audio           WASAPI playback and spectrum tap
    NEREA.Rendering       DirectX 12 renderer and GPU resources
    NEREA.App             WinUI 3 shell, MVVM, Windows integration and UI
  tests/NEREA.Smoke       Headless core validation
  installer/              Inno Setup installer definition
  tools/                  Local build and validation scripts
  .github/workflows/      Windows CI build/publish workflow
  NEREA.sln               Visual Studio solution
```

## Build on Windows

Requirements:

- Windows 10/11 x64
- .NET 8 SDK
- Visual Studio 2022 with WinUI/Windows App SDK support, or the matching Windows SDK/build tools
- Inno Setup 6 only if you want the installer locally

```powershell
./tools/build-local.ps1
```

This restores dependencies, validates HLSL entry points, runs the headless smoke tests, builds and publishes a self-contained `win-x64` application to `publish/NEREA`.

Open `NEREA.sln` in Visual Studio and select **NEREA.App / x64** for an interactive Windows run.

## GitHub Actions

`.github/workflows/build-windows.yml` builds on `windows-latest`, validates shaders, runs the smoke tests, publishes a self-contained Windows x64 build, creates `NEREA-win-x64.zip`, and builds `NEREA-Setup.exe` with Inno Setup.

No private signing certificates or keys are stored in the repository.

## Architecture

The UI does not render the aquatic scene. The DirectX renderer owns the realtime GPU work, the audio engine owns playback, and the core simulation owns fish behavior and audio analysis. The connection from audio to visuals is intentionally small and smoothed so NEREA behaves like an ecosystem rather than a beat visualizer.

### Rendering

- DirectX 12 via Vortice
- HLSL shaders
- GPU instancing
- procedural fish/body plans
- analytic water volume and underwater lighting
- caustics, rays, particles, plants, post-processing and ripple interaction
- adaptive quality and graceful WARP/fallback handling

### Ecosystem

- wandering
- schooling: alignment / cohesion / separation
- avoidance
- curiosity
- fear/scattering
- resting/hovering
- depth preference
- territory
- species-specific movement

### Audio

- MP3, WAV, FLAC, OGG, M4A/AAC and other formats supported by the selected decoder chain
- WASAPI playback
- metadata/artwork through TagLib#
- SQLite local library
- spectrum analysis with smoothing
- bass/mid/treble/energy driven ecosystem changes

### Modes

- Normal
- Ambient
- Cinematic
- Ultra / High / Balanced / Performance / Battery quality presets

## Branding

The official product name is **NEREA**. The aquatic environment is the visual hero. The main immersive screen uses only a small NEREA wordmark in the upper-left; music information and controls stay near the bottom and fade when idle. No large centered application title or logo is used.

The logo is a monochrome abstract aquatic mark based on ripple/wave/negative-space geometry. Branding assets are in `Assets/Branding` and the Windows `.ico` is in `Assets/Icons/NEREA.ico`.

## Offline-first

NEREA does not require an account, cloud service, or remote asset URL for its core experience. Music, settings, playlists, aquariums and library data remain local.

## Verification status

The repository includes a headless smoke-test project and Windows shader/project validation scripts. The provided development environment for this archive is Linux and does not contain the Windows .NET/WinUI toolchain, so a native Windows executable was **not** claimed as locally compiled here. The authoritative build test is the included Windows GitHub Actions workflow or a Windows/Visual Studio environment.

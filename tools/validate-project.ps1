$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$required = @(
  'NEREA.sln',
  'src/NEREA.App/NEREA.App.csproj',
  'src/NEREA.Core/NEREA.Core.csproj',
  'src/NEREA.Audio/NEREA.Audio.csproj',
  'src/NEREA.Rendering/NEREA.Rendering.csproj',
  'Assets/Icons/NEREA.ico',
  'Assets/Branding/nerea-symbol.svg',
  'Assets/Branding/nerea-wordmark.svg',
  'Assets/Fish/species.json',
  'Assets/Worlds/worlds.json',
  'tools/validate-shaders.ps1',
  'installer/NEREA.iss'
)
foreach ($p in $required) {
  if (-not (Test-Path (Join-Path $root $p))) { throw "Missing required project file: $p" }
}
if (Test-Path (Join-Path $root 'package.json')) { throw 'Web application residue detected: package.json' }
if (Test-Path (Join-Path $root 'index.html')) { throw 'Web application residue detected: index.html' }
Write-Host 'NEREA project structure is valid.'

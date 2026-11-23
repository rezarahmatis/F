# Local build + publish helper (Windows 10/11, .NET 8 SDK).
# Usage: ./tools/build-local.ps1 [-Configuration Release]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
  ./tools/validate-project.ps1
  dotnet restore src/NEREA.App/NEREA.App.csproj -r win-x64
  ./tools/validate-shaders.ps1
  dotnet run --project tests/NEREA.Smoke/NEREA.Smoke.csproj -c $Configuration
  dotnet build src/NEREA.App/NEREA.App.csproj -c $Configuration -r win-x64 --no-restore
  dotnet publish src/NEREA.App/NEREA.App.csproj -c $Configuration -r win-x64 --self-contained true -o publish/NEREA
  Write-Host "Done: $root\publish\NEREA\NEREA.exe"
} finally { Pop-Location }

# Compiles every NEREA shader entry point with FXC (the same compiler the app uses at runtime).
# Run on Windows: ./tools/validate-shaders.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$shaders = Join-Path $root 'Assets\Shaders'

# Locate fxc.exe: Windows SDK, or the Microsoft.Windows.SDK.BuildTools NuGet package
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" -ErrorAction SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1
if (-not $fxc) {
  $fxc = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\*\bin\*\x64\fxc.exe" -ErrorAction SilentlyContinue |
         Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $fxc) { throw 'fxc.exe not found (install the Windows 10/11 SDK).' }
Write-Host "Using $($fxc.FullName)"

$entries = @(
  @('Background.hlsl','VSBackground','vs_5_0'), @('Background.hlsl','PSBackground','ps_5_0'),
  @('Fish.hlsl','VSFish','vs_5_0'), @('Fish.hlsl','VSRay','vs_5_0'), @('Fish.hlsl','VSJelly','vs_5_0'),
  @('Fish.hlsl','PSCreature','ps_5_0'), @('Fish.hlsl','PSJelly','ps_5_0'),
  @('Plants.hlsl','VSPlant','vs_5_0'), @('Plants.hlsl','PSPlant','ps_5_0'),
  @('Particles.hlsl','VSParticle','vs_5_0'), @('Particles.hlsl','PSParticle','ps_5_0'),
  @('Post.hlsl','VSPost','vs_5_0'), @('Post.hlsl','PSPost','ps_5_0')
)

$common = Get-Content (Join-Path $shaders 'Common.hlsli') -Raw
$failed = 0
foreach ($e in $entries) {
  $file, $entry, $profile = $e
  $tmp = Join-Path ([IO.Path]::GetTempPath()) "nerea_$file"
  Set-Content $tmp ($common + "`n" + (Get-Content (Join-Path $shaders $file) -Raw))
  $out = & $fxc.FullName /nologo /T $profile /E $entry /O3 /Fo NUL $tmp 2>&1
  if ($LASTEXITCODE -ne 0) { Write-Host "FAILED $file $entry`n$out" -ForegroundColor Red; $failed++ }
  else { Write-Host "ok  $file $entry" }
}
if ($failed -gt 0) { throw "$failed shader entry point(s) failed to compile." }
Write-Host 'All shaders compile.'

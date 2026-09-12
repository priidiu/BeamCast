# Requires Windows 11 x64, .NET 10 SDK, Inno Setup 6.
# Output: artifacts\BeamCast-Setup.exe
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishDir = Join-Path $root "artifacts\win-x64"
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Publishing self-contained win-x64..."
dotnet publish src\AirNext.App\AirNext.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=false -p:PublishTrimmed=false `
  -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
  throw "Inno Setup 6 not found. Install: winget install JRSoftware.InnoSetup"
}

Write-Host "Compiling installer with $iscc..."
& $iscc /Q (Join-Path $root "installer\BeamCast.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $root "artifacts\BeamCast-Setup.exe"
if (-not (Test-Path $setup)) { throw "Missing $setup" }
Write-Host "OK $setup"

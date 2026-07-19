# Builds a complete release into the release/ directory:
#   - Plugin DLL          (MTGAEnhancementSuite.dll)
#   - Bootstrapper DLL    (already present, copied if newer)
#   - MSI installer       (MTGAPlus-Installer.msi — the one-click installer)
#   - Signed manifest     (manifest.json, signed with signing_key.pem)
#
# NOTE: the old self-contained EXE installer (which just ran `irm install.ps1
# | iex`) has been retired -- it tripped Defender for the same reason the MSI
# was built to avoid, and its behavior is still available via the PowerShell
# one-liner on the homepage. The MSI supersedes it.
#
# Usage:
#   tools\build_release.ps1 0.15.0
#
# Then upload everything in release/ to the GitHub release:
#   gh release create v0.15.0 release\* --title "..." --notes "..."

param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $repoRoot

$releaseDir = Join-Path $repoRoot "release"
if (-not (Test-Path $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir | Out-Null
}

Write-Host "=== Building plugin DLL ==="
dotnet build Plugin\MTGAEnhancementSuite.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }
Copy-Item Plugin\bin\Release\MTGAEnhancementSuite.dll $releaseDir\MTGAEnhancementSuite.dll -Force
Write-Host ""

Write-Host "=== Building bootstrapper DLL ==="
dotnet build Bootstrapper\Bootstrapper.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Bootstrapper build failed" }
Copy-Item Bootstrapper\bin\Release\MTGAESBootstrapper.dll $releaseDir\MTGAESBootstrapper.dll -Force
Write-Host ""

Write-Host "=== Bundling icons ==="
# Icon source PNGs live in assets\icons\ (tracked in git). We stage them
# into release\icons\ for local-deploy parity with what users see, then
# zip into release\icons.zip for distribution. They're NOT in the
# auto-update manifest — icons rarely change and the auto-updater only
# swaps DLLs anyway — but the fresh-install path needs them.
$iconsSrc = Join-Path $repoRoot "assets\icons"
$iconsStage = Join-Path $releaseDir "icons"
$iconsZip = Join-Path $releaseDir "icons.zip"
if (Test-Path $iconsZip)   { Remove-Item $iconsZip -Force }
if (Test-Path $iconsStage) { Remove-Item $iconsStage -Recurse -Force }
if (Test-Path $iconsSrc) {
    New-Item -ItemType Directory -Path $iconsStage -Force | Out-Null
    Copy-Item (Join-Path $iconsSrc "*.png") $iconsStage -Force
    Compress-Archive -Path (Join-Path $iconsStage "*.png") -DestinationPath $iconsZip -Force
    Write-Host "  icons.zip created from $iconsSrc ($(((Get-ChildItem $iconsSrc -Filter *.png).Count)) PNG(s))"
} else {
    Write-Host "  No assets\icons folder found; icons.zip will not be in release"
}
Write-Host ""

Write-Host "=== Building MSI installer ==="
# The MSI bundles BepInEx + the plugin and installs declaratively, so Windows
# Defender doesn't flag it like the self-contained EXE. Non-fatal: if WiX isn't
# installed we just skip it (the EXE + PS1 routes still ship).
try {
    & "$PSScriptRoot\build_msi.ps1" $Version
    if ($LASTEXITCODE -ne 0) { Write-Host "  MSI build returned $LASTEXITCODE - skipping" -ForegroundColor Yellow }
} catch {
    Write-Host "  MSI build skipped: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host '  Install WiX v5: dotnet tool install --global wix --version 5.0.2 ; wix extension add -g WixToolset.UI.wixext/5.0.2' -ForegroundColor Yellow
}
Write-Host ""

Write-Host "=== Signing manifest ==="
# manifest covers DLLs + config only — the auto-updater swaps DLLs at runtime,
# so the installers (MSI) and icons.zip are intentionally NOT in the manifest.
# They're just GitHub release assets for first-time installs.
python sign_release.py $Version
if ($LASTEXITCODE -ne 0) { throw "sign_release.py failed" }
Write-Host ""

Write-Host "=== Release contents ==="
Get-ChildItem $releaseDir | Format-Table Name, Length, LastWriteTime
Write-Host ""

Write-Host "Release v$Version is ready. Upload with:"
Write-Host "  gh release create v$Version release\* --title `"v$Version - <title>`" --notes `"<notes>`""
Write-Host "or, if the release already exists:"
Write-Host "  gh release upload v$Version release\MTGAPlus-Installer.msi"

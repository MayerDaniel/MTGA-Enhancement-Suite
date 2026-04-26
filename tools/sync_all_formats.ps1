# Syncs all format legal card lists to Firebase.
# Usage: .\tools\sync_all_formats.ps1 [-ServiceAccount path\to\key.json]

param(
    [string]$ServiceAccount = ""
)

$ErrorActionPreference = "Stop"
$FunctionsUrl = "https://us-central1-mtga-enhancement-suite.cloudfunctions.net"
$Project = "mtga-enhancement-suite"
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step($msg) {
    Write-Host ""
    Write-Host "===> $msg" -ForegroundColor Cyan
}

function Invoke-Sync($format) {
    Write-Step "Syncing $format..."
    try {
        $resp = curl.exe -X POST "$FunctionsUrl/syncFormatsHttp?format=$format" `
            -H "Content-Type: application/json" `
            -H "Content-Length: 0" `
            --data "" `
            -m 540
        Write-Host $resp
    } catch {
        Write-Host "  WARNING: $format sync request failed or timed out (function may still be running)" -ForegroundColor Yellow
    }
}

# Step 1: Deploy functions
Write-Step "Deploying Firebase functions"
Set-Location "$RepoRoot\firebase"
firebase deploy --only functions --project $Project
if ($LASTEXITCODE -ne 0) {
    Write-Host "Firebase deploy failed, aborting." -ForegroundColor Red
    exit 1
}

# Step 2: Sync Scryfall-based formats one at a time
Set-Location $RepoRoot
$ScryfallFormats = @("pauper", "standardpauper", "planarstandard", "modern")
foreach ($fmt in $ScryfallFormats) {
    Invoke-Sync $fmt
}

# Step 3: Sync Historic Pauper from MTGA DB
Write-Step "Syncing historicpauper from MTGA local DB"
$pyArgs = @("tools/sync_pauper_from_mtga.py")
if ($ServiceAccount) {
    $pyArgs += "--service-account"
    $pyArgs += $ServiceAccount
} else {
    Write-Host "  WARNING: No --ServiceAccount provided. Script will likely fail to upload." -ForegroundColor Yellow
    Write-Host "  Pass -ServiceAccount path\to\key.json to authenticate." -ForegroundColor Yellow
}
python $pyArgs

Write-Host ""
Write-Host "Done." -ForegroundColor Green

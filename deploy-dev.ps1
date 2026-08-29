<#
    deploy-dev.ps1 - publish and run the DEV instance.

    A second, isolated copy of the app for testing:
      * port 5001                (prod stays on 5000, untouched)
      * own database             J:\GhafAnalytika\bix-dev-data\analytika.db (a copy)
      * own publish folder       J:\GhafAnalytika\bix-dev
      * own Data Protection keys (inside its data dir)
      * runs as a plain detached process - no Windows service, so it can never be
        confused with the BixApp service or picked up by deploy-core.ps1.

    Nothing here touches prod: different port, different folder, different database.

    Usage:
      .\deploy-dev.ps1              # publish + start detached, wait for healthz
      .\deploy-dev.ps1 -NoBuild     # just start what is already published
      .\deploy-dev.ps1 -Foreground  # run attached (Ctrl+C to stop)
      .\deploy-dev.ps1 -Stop        # stop the running dev instance

    ASCII-only on purpose (Windows PowerShell 5.1 parsing).
#>
[CmdletBinding()]
param([switch]$NoBuild, [switch]$Stop, [switch]$Foreground)

$ErrorActionPreference = 'Stop'
$Project    = 'J:\Ghaf BI\analytika-rcm\Analytika\Analytika.csproj'
$PublishDir = 'J:\GhafAnalytika\bix-dev'
$DataDir    = 'J:\GhafAnalytika\bix-dev-data'
$Port       = 5001
$Url        = "http://localhost:$Port"

function Write-Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

function Get-DevProcess {
    Get-CimInstance Win32_Process -Filter "Name='Analytika.exe' OR Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*bix-dev*" }
}

# ---- Stop -----------------------------------------------------------------
if ($Stop) {
    Write-Step 'Stopping dev instance'
    $p = Get-DevProcess
    if ($p) { $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Write-Host 'Dev instance stopped.' -ForegroundColor Green }
    else    { Write-Host 'Dev instance was not running.' -ForegroundColor Yellow }
    return
}

# ---- Guard: never point dev at the production database ---------------------
# Dev runs on a FRESH database created by the app on first start (schema + seed).
# Copying prod was rejected as impractical: the live DB is 83GB of inline XML blobs
# and an online backup measured 2.4 MB/s under load - a 9.5 hour copy.
if ($DataDir -like '*GhafAnalytika\Analytika*') {
    Write-Host 'Refusing to start: DataDir points at the PRODUCTION database folder.' -ForegroundColor Red
    exit 1
}
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
if (-not (Test-Path (Join-Path $DataDir 'analytika.db'))) {
    Write-Host "No dev database yet - the app will create a fresh one in $DataDir" -ForegroundColor Yellow
}

# ---- Stop any previous dev instance ---------------------------------------
$existing = Get-DevProcess
if ($existing) { Write-Step 'Stopping previous dev instance'; $existing | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Start-Sleep -Seconds 2 }

# ---- Publish ---------------------------------------------------------------
if (-not $NoBuild) {
    Write-Step 'Publishing (Release) to dev folder'
    & dotnet publish $Project -c Release -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    Write-Host 'Publish succeeded.' -ForegroundColor Green
}

# ---- Dev-only settings -----------------------------------------------------
# Written after publish so it is not overwritten by the publish output.
#
# The port and data directory are pinned HERE as well as in the repo's
# appsettings.Development.json, because this file replaces that one. Leaving them out
# was how a dev process ended up with no endpoint of its own: started without
# ASPNETCORE_URLS it fell back to :5000, collided with the BixApp service, and sat
# there alive but serving nothing.

# This file is rewritten on every deploy, so anything hand-added to it would be lost.
# Carry the Anthropic key across: it is what powers the dev assistant, and having it
# silently disappear on the next publish would look like the chat breaking at random.
$existingSettings = Join-Path $PublishDir 'appsettings.Development.json'
$apiKey = $null
if (Test-Path $existingSettings) {
    try { $apiKey = (Get-Content $existingSettings -Raw | ConvertFrom-Json).Anthropic.ApiKey } catch { }
}

$devSettings = @{
    Kestrel            = @{ Endpoints = @{ Http = @{ Url = $Url } } }
    Data               = @{ Dir = $DataDir; RefuseDirs = @('J:\GhafAnalytika\Analytika') }
    Anthropic          = @{ ApiKey = $apiKey }   # preserved across deploys; set it once
    DevCli             = @{ Enabled = $true }   # in-app console at /DevCli
    DetailedErrors     = $true
    DevTools           = @{ Enabled = $true }
    Keepalive          = @{ Enabled = $false }   # no tunnel to keep warm
    Warmup             = @{ Enabled = $false }   # don't burn dev I/O on cache warm-up
    BackgroundJobs     = @{ PendingDownloads = @{ HostedServiceEnabled = $false } }
    Serilog            = @{ MinimumLevel = @{ Default = 'Information' } }
    # Fresh DB: let startup build the schema and seed the admin/roles so the app is usable.
    StartupMaintenance = @{ RunDatabaseSetupOnStartup = $true; CreateIndexesOnStartup = $true; SeedDataOnStartup = $true }
} | ConvertTo-Json -Depth 6
Set-Content -Path (Join-Path $PublishDir 'appsettings.Development.json') -Value $devSettings -Encoding UTF8

Write-Step "Starting dev instance on $Url"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# DB_DIR is a machine-wide variable on this host pointing at the PRODUCTION data folder,
# and a child process inherits it. Clear it for this process tree so dev's own Data:Dir
# is the only thing in play.
$env:DB_DIR = $null
$env:ASPNETCORE_URLS = $null

Write-Host "  database : $DataDir\analytika.db (isolated)" -ForegroundColor Gray
Write-Host "  console  : $Url/DevCli" -ForegroundColor Gray
Write-Host "  prod     : http://localhost:5000 (untouched)" -ForegroundColor Gray

if ($Foreground) {
    Write-Host "  press Ctrl+C to stop`n" -ForegroundColor Gray
    & (Join-Path $PublishDir 'Analytika.exe')
    return
}

# Detached by default: a foreground process dies with the terminal that launched it,
# which is how dev kept disappearing between sessions.
$log = Join-Path $DataDir 'dev.log'
Start-Process -FilePath (Join-Path $PublishDir 'Analytika.exe') `
              -WorkingDirectory $PublishDir `
              -RedirectStandardOutput $log `
              -RedirectStandardError (Join-Path $DataDir 'dev.err.log') `
              -WindowStyle Hidden

Write-Host "  log      : $log" -ForegroundColor Gray
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    try {
        $code = (Invoke-WebRequest "$Url/healthz" -UseBasicParsing -TimeoutSec 5).StatusCode
        if ($code -eq 200) { Write-Host "`nDev is live at $Url (healthz 200)." -ForegroundColor Green; return }
    } catch { }
}
Write-Host "`nDev did not answer /healthz within 60s - check $log" -ForegroundColor Red
exit 1

<#
    deploy.ps1 - one-command deploy for the Bix app.

    Stops the BixApp Windows service, republishes the app to the live folder,
    restarts the service, and verifies health. Self-elevates to Administrator
    (the service stop/start requires it).

    ASCII-only on purpose: Windows PowerShell 5.1 mis-parses non-ASCII (emoji,
    box-drawing) in .ps1 files that lack a UTF-8 BOM. Keep it plain ASCII.

    Usage (from any PowerShell - prompts for elevation if needed):
        .\deploy.ps1
        .\deploy.ps1 -SkipHealthCheck

    Safe by design: publish runs in try/finally, so the service is ALWAYS
    restarted (and re-enabled) even if the build fails - the site never gets
    stranded stopped. A slow-exiting app host is force-killed so the DLL copy
    never fails on a lock.
#>

[CmdletBinding()]
param(
    [switch]$SkipHealthCheck
)

$ErrorActionPreference = 'Stop'

# ---- Config -------------------------------------------------------------
$ServiceName = 'BixApp'
$Project     = 'J:\Ghaf BI\analytika-rcm\Analytika\Analytika.csproj'
$PublishDir  = 'J:\GhafAnalytika\bix-app'
$HealthUrl   = 'http://127.0.0.1:5000/healthz'
$PublicUrl   = 'https://bix.ghafservices.com'

# ---- Self-elevate to Administrator --------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent() `
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($SkipHealthCheck) { $argList += '-SkipHealthCheck' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
    return
}

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Get-AppHosts {
    # App hosts run in Session 0 (the Services session): the NSSM-launched
    # 'dotnet.exe Analytika.dll', plus any orphaned 'Analytika.exe' apphost
    # left behind by earlier runs. Their command lines are usually NOT readable
    # via CIM for Session-0 processes, so we match by Session 0 + process name.
    # Interactive builds / this script run in Session 1+, so they're excluded.
    # This clears ALL competing app instances so the DB isn't contended and the
    # published DLL is never locked at publish time.
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Analytika.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq 0 -or $_.CommandLine -like '*Analytika.dll*' }
}

# ---- Locate nssm (5.1 compatible) ---------------------------------------
$nssmCmd = Get-Command nssm -ErrorAction SilentlyContinue
$nssm = if ($nssmCmd) { $nssmCmd.Source } else { $null }
if (-not $nssm) {
    foreach ($candidate in @(
        'C:\ProgramData\chocolatey\bin\nssm.exe',
        "$env:ProgramFiles\nssm\win64\nssm.exe",
        "${env:ProgramFiles(x86)}\nssm\win64\nssm.exe")) {
        if (Test-Path $candidate) { $nssm = $candidate; break }
    }
}
if (-not $nssm) {
    Write-Host 'nssm not found. Install it (winget install NSSM.NSSM) or add it to PATH.' -ForegroundColor Red
    exit 1
}

# ---- 1. Stop the service + ensure the app host actually exits ------------
Write-Step "Stopping $ServiceName"
& $nssm stop $ServiceName 2>&1 | Out-Host

# NSSM's graceful stop returns before the .NET host exits (slow shutdown).
# Wait for it to release the DLL; force-kill after a timeout so the publish
# copy never fails on a locked Analytika.dll.
$deadline = (Get-Date).AddSeconds(20)
while (((Get-Date) -lt $deadline) -and (Get-AppHosts)) { Start-Sleep -Seconds 1 }
$stragglers = Get-AppHosts
if ($stragglers) {
    Write-Host 'App host still running after stop - force-killing so the DLL unlocks...' -ForegroundColor Yellow
    $stragglers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

$published = $false
try {
    # ---- 2. Publish -----------------------------------------------------
    Write-Step 'Publishing (Release)'
    & dotnet publish $Project -c Release -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    $published = $true
    Write-Host 'Publish succeeded.' -ForegroundColor Green
}
catch {
    Write-Host "PUBLISH FAILED: $_" -ForegroundColor Red
    Write-Host 'Restarting the service on the PREVIOUS build so the site stays up...' -ForegroundColor Yellow
}
finally {
    # ---- 3. Always re-enable + restart the service ----------------------
    # A prior interrupted stop can leave the service DISABLED, making 'start'
    # fail with "the service cannot be started because it is disabled".
    # Force it back to automatic every time.
    Write-Step "Starting $ServiceName"
    & sc.exe config $ServiceName start= auto | Out-Null
    & $nssm start $ServiceName
}

if (-not $published) {
    Write-Host "`nDeploy aborted - old build is still serving. Fix the build error and re-run." -ForegroundColor Red
    exit 1
}

# ---- 4. Health check ----------------------------------------------------
if ($SkipHealthCheck) {
    Write-Host "`nDeploy complete (health check skipped)." -ForegroundColor Green
    return
}

Write-Step 'Verifying health'
$ok = $false
$elapsed = 0
for ($i = 1; $i -le 30; $i++) {
    try {
        $r = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 3 -UseBasicParsing
        if ($r.StatusCode -eq 200) { $ok = $true; $elapsed = $i; break }
    } catch { }
    Start-Sleep -Seconds 1
}

if ($ok) {
    Write-Host "Local health: 200 OK (after $elapsed s)" -ForegroundColor Green
    try {
        $p = Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 15 -UseBasicParsing -MaximumRedirection 5
        Write-Host "Public URL ($PublicUrl): $($p.StatusCode)" -ForegroundColor Green
    } catch {
        Write-Host "Public URL check inconclusive (tunnel may need a moment): $($_.Exception.Message)" -ForegroundColor Yellow
    }
    Write-Host "`nDeploy complete." -ForegroundColor Green
} else {
    $logPath = Join-Path $PublishDir 'logs\stderr.log'
    Write-Host "`nApp did not report healthy within 30s. Check: nssm status $ServiceName  and  $logPath" -ForegroundColor Red
    exit 1
}

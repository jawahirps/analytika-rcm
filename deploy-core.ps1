<#
    deploy-core.ps1 - the actual deploy, with NO self-elevation.

    Stops BixApp, republishes to the live folder, restarts BixApp. Assumes the
    caller already has permission to stop/start the service - run
    setup-autodeploy.ps1 ONCE (elevated) to grant the current user that right,
    after which this script (and Claude) can deploy silently, no UAC prompt.

    ASCII-only on purpose (Windows PowerShell 5.1 parsing).
#>
[CmdletBinding()]
param([switch]$SkipHealthCheck)

$ErrorActionPreference = 'Stop'
$ServiceName = 'BixApp'
$Project     = 'J:\Ghaf BI\analytika-rcm\Analytika\Analytika.csproj'
$PublishDir  = 'J:\GhafAnalytika\bix-app'
$HealthUrl   = 'http://127.0.0.1:5000/healthz'

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

function Get-AppHosts {
    # Match ONLY the app host (Analytika.dll / Analytika.exe). The old
    # "SessionId -eq 0" clause force-killed unrelated dotnet processes
    # (e.g. the long-running ParseAll backfill) on every deploy.
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Analytika.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Analytika.exe' -or $_.CommandLine -like '*Analytika.dll*' }
}

# ---- Locate nssm --------------------------------------------------------
$nssm = (Get-Command nssm -ErrorAction SilentlyContinue).Source
if (-not $nssm) {
    foreach ($c in @('C:\ProgramData\chocolatey\bin\nssm.exe',
                     "$env:ProgramFiles\nssm\win64\nssm.exe",
                     "${env:ProgramFiles(x86)}\nssm\win64\nssm.exe")) {
        if (Test-Path $c) { $nssm = $c; break }
    }
}

# ---- 1. Stop service + ensure host exits --------------------------------
Write-Step "Stopping $ServiceName"
if ($nssm) { & $nssm stop $ServiceName 2>&1 | Out-Host } else { & sc.exe stop $ServiceName | Out-Host }

$deadline = (Get-Date).AddSeconds(20)
while (((Get-Date) -lt $deadline) -and (Get-AppHosts)) { Start-Sleep -Seconds 1 }
$stragglers = Get-AppHosts
if ($stragglers) {
    Write-Host 'Force-killing lingering app host so the DLL unlocks...' -ForegroundColor Yellow
    $stragglers | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

$published = $false
try {
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
    Write-Step "Starting $ServiceName"
    & sc.exe config $ServiceName start= auto | Out-Null
    if ($nssm) { & $nssm start $ServiceName } else { & sc.exe start $ServiceName }
}

if (-not $published) { Write-Host "`nDeploy aborted - old build still serving." -ForegroundColor Red; exit 1 }

if ($SkipHealthCheck) { Write-Host "`nDeploy complete (health check skipped)." -ForegroundColor Green; return }

Write-Step 'Verifying health'
$ok = $false
for ($i = 1; $i -le 30; $i++) {
    try { if ((Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 3 -UseBasicParsing).StatusCode -eq 200) { $ok = $true; break } } catch { }
    Start-Sleep -Seconds 1
}
if ($ok) { Write-Host "`nDeploy complete - local health 200 OK." -ForegroundColor Green }
else     { Write-Host "`nApp did not report healthy within 30s." -ForegroundColor Red; exit 1 }

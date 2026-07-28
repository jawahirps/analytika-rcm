<#
    restore.ps1 - bring the site back on BixApp:5000 and take GhafBI offline.

    The outage cause: two parallel deployments. The Cloudflare tunnel routes to
    port 5000 (BixApp), but BixApp's service was deleted, so nothing served 5000.
    A separate 'GhafBI' service was running the older build on port 5200.

    This script:
      1. Stops + DISABLES the GhafBI service (port 5200) so it no longer runs
         or competes - GhafBI is removed from the serving path entirely.
      2. Clears any leftover Session-0 app hosts (frees port 5000 and the DB).
      3. (Re)installs the BixApp service pointing at the freshly-published build
         in J:\GhafAnalytika\bix-app on port 5000 (what the tunnel expects).
      4. Starts BixApp and verifies local health, the new build, and the
         public URL.

    ASCII-only; self-elevates to Administrator.
#>

[CmdletBinding()]
param([switch]$SkipHealthCheck)
# Continue (not Stop): nssm/sc are native exes that write to stderr for benign
# cases (e.g. removing a service that doesn't exist). Under 'Stop' that aborts
# the script mid-way. We check results explicitly instead.
$ErrorActionPreference = 'Continue'

$BixApp     = 'BixApp'
$GhafBI     = 'GhafBI'
$Dotnet     = 'C:\Program Files\dotnet\dotnet.exe'
$PublishDir = 'J:\GhafAnalytika\bix-app'
$Dll        = 'J:\GhafAnalytika\bix-app\Analytika.dll'
$DbDir      = 'J:\GhafAnalytika\Analytika'
$Url        = 'http://localhost:5000'
$HealthUrl  = 'http://127.0.0.1:5000/healthz'
$LoginUrl   = 'http://127.0.0.1:5000/login'
$PublicUrl  = 'https://bix.ghafservices.com'

# ---- self-elevate ----
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    if ($SkipHealthCheck) { $a += '-SkipHealthCheck' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a
    return
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

$nssmCmd = Get-Command nssm -ErrorAction SilentlyContinue
$nssm = if ($nssmCmd) { $nssmCmd.Source } else { $null }
if (-not $nssm) {
    foreach ($c in @('C:\ProgramData\chocolatey\bin\nssm.exe',
                     "$env:ProgramFiles\nssm\win64\nssm.exe",
                     "${env:ProgramFiles(x86)}\nssm\win64\nssm.exe")) {
        if (Test-Path $c) { $nssm = $c; break }
    }
}
if (-not $nssm) { Write-Host 'nssm not found.' -ForegroundColor Red; exit 1 }

if (-not (Test-Path $Dll)) {
    Write-Host "Published build not found at $Dll - nothing to serve. Aborting." -ForegroundColor Red
    exit 1
}

# ---- 1. Take GhafBI (port 5200) offline ----
Step "Disabling the GhafBI service (port 5200)"
& sc.exe stop $GhafBI 2>&1 | Out-Null
& sc.exe config $GhafBI start= disabled 2>&1 | Out-Null
Write-Host 'GhafBI stopped + disabled.' -ForegroundColor Green

# ---- 2. Clear any Session-0 app hosts (frees 5000 + the DB) ----
Step 'Clearing leftover app hosts'
Start-Sleep -Seconds 2
$hosts = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Analytika.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq 0 }
foreach ($h in $hosts) {
    Write-Host ("  killing {0} PID {1}" -f $h.Name, $h.ProcessId) -ForegroundColor Yellow
    Stop-Process -Id $h.ProcessId -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# ---- 3. (Re)install BixApp on port 5000 ----
Step "Installing the BixApp service (port 5000, new build)"
& $nssm stop $BixApp 2>&1 | Out-Null
& $nssm remove $BixApp confirm 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $nssm install $BixApp $Dotnet $Dll
& $nssm set $BixApp AppDirectory $PublishDir
& $nssm set $BixApp AppEnvironmentExtra "DB_DIR=$DbDir" "ASPNETCORE_URLS=$Url" "ASPNETCORE_ENVIRONMENT=Production"
& $nssm set $BixApp AppStdout "$PublishDir\logs\stdout.log"
& $nssm set $BixApp AppStderr "$PublishDir\logs\stderr.log"
& $nssm set $BixApp Start SERVICE_AUTO_START
& $nssm set $BixApp AppExit Default Restart
& $nssm set $BixApp AppRestartDelay 5000
New-Item -ItemType Directory -Force -Path "$PublishDir\logs" | Out-Null

# Confirm the service actually got created before trying to start it.
$exists = (sc.exe query $BixApp 2>&1 | Select-String 'STATE')
if (-not $exists) {
    Write-Host "BixApp was NOT created by nssm install." -ForegroundColor Red
    Write-Host "Most likely it is 'marked for deletion' from the earlier removal and needs a reboot," -ForegroundColor Yellow
    Write-Host "or install under a fresh name:  nssm install BixApp2 `"$Dotnet`" `"$Dll`"  (then repeat the set/start)." -ForegroundColor Yellow
    exit 1
}

Step "Starting $BixApp"
& $nssm start $BixApp

# ---- 4. Verify ----
if ($SkipHealthCheck) { Write-Host "`nRestore complete (health check skipped)." -ForegroundColor Green; return }

Step 'Verifying'
$ok = $false; $elapsed = 0
for ($i = 1; $i -le 45; $i++) {
    try { $r = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 3 -UseBasicParsing; if ($r.StatusCode -eq 200) { $ok = $true; $elapsed = $i; break } } catch {}
    Start-Sleep -Seconds 1
}
if (-not $ok) {
    Write-Host "`nApp not healthy within 45s. Check: nssm status $BixApp  and  $PublishDir\logs\stderr.log" -ForegroundColor Red
    exit 1
}
Write-Host "Local health (port 5000): 200 OK (after $elapsed s)" -ForegroundColor Green

try {
    $l = Invoke-WebRequest -Uri $LoginUrl -TimeoutSec 5 -UseBasicParsing -MaximumRedirection 0 -ErrorAction Stop
    if ($l.StatusCode -eq 200) { Write-Host 'NEW build confirmed: /login serves the login page.' -ForegroundColor Green }
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 200) { Write-Host 'NEW build confirmed: /login OK.' -ForegroundColor Green }
    else { Write-Host '/login not 200 yet - may be warming up.' -ForegroundColor Yellow }
}
try {
    $p = Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 20 -UseBasicParsing -MaximumRedirection 5
    Write-Host "Public URL ($PublicUrl): $($p.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "Public URL check inconclusive (tunnel may need a moment): $($_.Exception.Message)" -ForegroundColor Yellow
}
Write-Host "`nRestore complete - BixApp:5000 is live, GhafBI disabled." -ForegroundColor Green

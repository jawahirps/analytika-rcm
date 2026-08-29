<#
    recover.ps1 - clean-up + deploy in one elevated run.

    Fixes the tangled state left by repeated partial deploys:
      1. Stops + disables the DEAD cloudflared token-tunnel service (da8a96d0),
         which is stuck retrying a rotated token and burning CPU. The WORKING
         'analytika' tunnel is a separate process and keeps serving the site.
      2. Kills ALL Session-0 app hosts (orphaned dotnet.exe + Analytika.exe)
         so the 48 GB SQLite DB is no longer contended and the published DLL
         is never locked at publish time.
      3. Re-enables the BixApp service (a failed stop can leave it DISABLED),
         republishes the app, and starts ONE clean instance.
      4. Verifies local health, which build is live, and the public URL.

    ASCII-only (Windows PowerShell 5.1 mis-parses non-ASCII .ps1 without a BOM).
    Self-elevates to Administrator.

    Usage:  .\recover.ps1
#>

[CmdletBinding()]
param([switch]$SkipHealthCheck)

$ErrorActionPreference = 'Stop'

$ServiceName    = 'BixApp'
$TunnelService  = 'cloudflared'
$Project        = 'J:\Ghaf BI\analytika-rcm\Analytika\Analytika.csproj'
$PublishDir     = 'J:\GhafAnalytika\bix-app'
$HealthUrl      = 'http://127.0.0.1:5000/healthz'
$LoginUrl       = 'http://127.0.0.1:5000/login'
$PublicUrl      = 'https://bix.ghafservices.com'

# ---- Self-elevate -------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent() `
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    if ($SkipHealthCheck) { $a += '-SkipHealthCheck' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $a
    return
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

function Get-AppHosts {
    # Session-0 app hosts: NSSM's 'dotnet Analytika.dll' + any orphaned
    # Analytika.exe apphost. Command lines are usually unreadable for Session-0
    # processes, so match by Session 0. Interactive builds run in Session 1+.
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Analytika.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq 0 -or $_.CommandLine -like '*Analytika.dll*' }
}

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

# ---- 1. Stop + disable the dead cloudflared token-tunnel service ---------
# This serves the broken da8a96d0 tunnel (rotated token). It does NOT serve
# bix.ghafservices.com (that goes through the separate working 'analytika'
# tunnel process), so stopping it is safe and just frees CPU.
Step "Stopping the dead cloudflared token-tunnel service"
try {
    & sc.exe stop $TunnelService  | Out-Null
    & sc.exe config $TunnelService start= disabled | Out-Null
    Write-Host 'cloudflared service stopped + disabled (broken token tunnel).' -ForegroundColor Green
} catch {
    Write-Host "cloudflared service change skipped: $_" -ForegroundColor Yellow
}

# ---- 2. Stop BixApp + kill ALL Session-0 app hosts -----------------------
Step "Stopping $ServiceName and clearing orphaned app hosts"
& $nssm stop $ServiceName 2>&1 | Out-Host
$deadline = (Get-Date).AddSeconds(20)
while (((Get-Date) -lt $deadline) -and (Get-AppHosts)) { Start-Sleep -Seconds 1 }
$stragglers = Get-AppHosts
if ($stragglers) {
    foreach ($p in $stragglers) {
        Write-Host ("  force-killing {0} PID {1} (Session {2})" -f $p.Name, $p.ProcessId, $p.SessionId) -ForegroundColor Yellow
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

# ---- 3. Re-enable + publish + start --------------------------------------
$published = $false
try {
    Step 'Publishing (Release)'
    & dotnet publish $Project -c Release -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
    $published = $true
    Write-Host 'Publish succeeded.' -ForegroundColor Green
}
catch {
    Write-Host "PUBLISH FAILED: $_" -ForegroundColor Red
    Write-Host 'Restarting on the previous build so the site stays up...' -ForegroundColor Yellow
}
finally {
    Step "Starting $ServiceName"
    & sc.exe config $ServiceName start= auto | Out-Null
    & $nssm start $ServiceName
}

if (-not $published) {
    Write-Host "`nDeploy aborted - old build still serving. Fix the build error and re-run." -ForegroundColor Red
    exit 1
}

# ---- 4. Verify ----------------------------------------------------------
if ($SkipHealthCheck) { Write-Host "`nRecovery complete (health check skipped)." -ForegroundColor Green; return }

Step 'Verifying'
$ok = $false; $elapsed = 0
for ($i = 1; $i -le 40; $i++) {
    try { $r = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 3 -UseBasicParsing; if ($r.StatusCode -eq 200) { $ok = $true; $elapsed = $i; break } } catch {}
    Start-Sleep -Seconds 1
}
if (-not $ok) {
    Write-Host "`nApp did not report healthy within 40s. Check: nssm status $ServiceName  and  $PublishDir\logs\stderr.log" -ForegroundColor Red
    exit 1
}
Write-Host "Local health: 200 OK (after $elapsed s)" -ForegroundColor Green

# Is the NEW build live? /login only exists on the new build.
try {
    $l = Invoke-WebRequest -Uri $LoginUrl -TimeoutSec 5 -UseBasicParsing -MaximumRedirection 0 -ErrorAction Stop
    if ($l.StatusCode -eq 200) { Write-Host 'NEW build confirmed: /login serves the login page.' -ForegroundColor Green }
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 200) { Write-Host 'NEW build confirmed: /login OK.' -ForegroundColor Green }
    else { Write-Host '/login not 200 yet - may still be the old build or warming up.' -ForegroundColor Yellow }
}
try {
    $p = Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 20 -UseBasicParsing -MaximumRedirection 5
    Write-Host "Public URL ($PublicUrl): $($p.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "Public URL check inconclusive: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Report leftover app hosts (should be exactly one, the fresh service instance)
$hosts = Get-AppHosts
Write-Host ("`nApp host instances now running: {0} (expect 1)" -f ($hosts | Measure-Object).Count) -ForegroundColor Cyan
Write-Host "`nRecovery complete." -ForegroundColor Green

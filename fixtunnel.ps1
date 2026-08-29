<#
    fixtunnel.ps1 - restart the Cloudflare tunnel so it reloads config.yml.

    The tunnel config (C:\Users\jawah\.cloudflared\config.yml) now points
    bix.ghafservices.com -> http://localhost:5000 (where BixApp serves the new
    build). The running cloudflared connector still has the old :5200 target in
    memory, causing 502s. cloudflared only reads ingress config at startup, so
    this stops all cloudflared processes and starts a fresh one that reads the
    corrected config.

    ASCII-only; self-elevates.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'

$Cloudflared = 'C:\Program Files (x86)\cloudflared\cloudflared.exe'
$Config      = 'C:\Users\jawah\.cloudflared\config.yml'
$PublicUrl   = 'https://bix.ghafservices.com/login'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    return
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

if (-not (Test-Path $Cloudflared)) {
    $alt = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
    if ($alt) { $Cloudflared = $alt } else { Write-Host 'cloudflared.exe not found.' -ForegroundColor Red; exit 1 }
}

# The dead token-tunnel service should stay disabled; make sure it is.
Step 'Ensuring the broken token-tunnel service stays disabled'
& sc.exe stop cloudflared 2>&1 | Out-Null
& sc.exe config cloudflared start= disabled 2>&1 | Out-Null

# Stop every cloudflared connector (stale config in memory).
Step 'Stopping all cloudflared connectors'
Get-Process cloudflared -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  killing cloudflared PID {0}" -f $_.Id) -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# Start a fresh connector that reads the corrected config (-> :5000).
Step 'Starting a fresh tunnel connector (reads config.yml -> :5000)'
$logDir = 'J:\GhafAnalytika\bix-app\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Start-Process -FilePath $Cloudflared `
    -ArgumentList @('tunnel','--config',"`"$Config`"",'run') `
    -WindowStyle Hidden `
    -RedirectStandardOutput "$logDir\cloudflared.out.log" `
    -RedirectStandardError  "$logDir\cloudflared.err.log"
Write-Host 'Fresh cloudflared started.' -ForegroundColor Green

# Verify the public URL comes back.
Step 'Verifying public URL'
$ok = $false
for ($i = 1; $i -le 20; $i++) {
    try {
        $r = Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 8 -UseBasicParsing -MaximumRedirection 5
        if ($r.StatusCode -eq 200) { $ok = $true; Write-Host "  $PublicUrl -> 200 (after $($i*3)s)" -ForegroundColor Green; break }
        else { Write-Host "  attempt $i -> $($r.StatusCode)" -ForegroundColor Yellow }
    } catch { Write-Host "  attempt $i -> $($_.Exception.Message)" -ForegroundColor DarkYellow }
    Start-Sleep -Seconds 3
}
if ($ok) { Write-Host "`nTunnel fixed - bix.ghafservices.com is live on the new build." -ForegroundColor Green }
else {
    Write-Host "`nStill not 200. Check $logDir\cloudflared.err.log for the connector status." -ForegroundColor Red
    Write-Host "NOTE: this manual connector is not durable across reboot - once stable, reinstall it as a service." -ForegroundColor Yellow
}

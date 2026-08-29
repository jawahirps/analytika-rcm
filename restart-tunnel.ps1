<#
    restart-tunnel.ps1 - clean-restart the Cloudflare tunnel connector.

    After a build swap the site is healthy locally on :5000, but the public URL
    502s because a STALE leftover cloudflared connector (started earlier with an
    old :5200 origin config in memory) is still registered on the tunnel and the
    edge routes to it. This stops the durable CloudflaredBix service, kills EVERY
    cloudflared process (clears strays), then starts ONLY the service again so a
    single connector with the correct :5000 config registers. Verifies the public
    URL comes back.

    ASCII-only; self-elevates.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'

$svc       = 'CloudflaredBix'
$publicUrl = 'https://bix.ghafservices.com/login'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    return
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

Step "Stopping $svc"
& sc.exe stop $svc 2>&1 | Out-Null
Start-Sleep -Seconds 2

Step 'Killing every cloudflared process (clears stale connectors)'
Get-Process cloudflared -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host ("  killing cloudflared PID {0}" -f $_.Id) -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

Step "Starting $svc (single clean connector -> :5000)"
& sc.exe config $svc start= auto | Out-Null
& sc.exe start $svc 2>&1 | Out-Null

Step 'Verifying public URL'
$ok = $false
for ($i = 1; $i -le 20; $i++) {
    try {
        $r = Invoke-WebRequest -Uri $publicUrl -TimeoutSec 8 -UseBasicParsing -MaximumRedirection 5
        if ($r.StatusCode -eq 200) { $ok = $true; Write-Host "  $publicUrl -> 200 (after ~$($i*3)s)" -ForegroundColor Green; break }
        else { Write-Host "  attempt $i -> $($r.StatusCode)" -ForegroundColor Yellow }
    } catch { Write-Host "  attempt $i -> $($_.Exception.Message)" -ForegroundColor DarkYellow }
    Start-Sleep -Seconds 3
}

if ($ok) { Write-Host "`nTunnel restarted - bix.ghafservices.com is live on the new build." -ForegroundColor Green }
else     { Write-Host "`nStill not 200. Check 'sc query $svc' and the cloudflared logs." -ForegroundColor Red }

<#
    disable-old-tunnel.ps1 - stop the stale GhafBITunnel connector.

    Two services connect the SAME Cloudflare tunnel (cc5c75c4):
      * CloudflaredBix  -> http://localhost:5000  (correct, new build)
      * GhafBITunnel    -> http://localhost:5200  (stale; :5200 is offline -> 502)
    Cloudflare round-robins between connectors, so ~half of requests 502.
    This stops + DISABLES GhafBITunnel, then clean-restarts CloudflaredBix so a
    single correct connector serves the tunnel. Verifies public stability.

    ASCII-only; self-elevates.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'

$old       = 'GhafBITunnel'
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

Step "Stopping + disabling $old (stale -> :5200)"
& sc.exe stop $old 2>&1 | Out-Null
& sc.exe config $old start= disabled 2>&1 | Out-Null

Step 'Killing all cloudflared, then restarting the good connector'
Get-Process cloudflared -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
& sc.exe config $svc start= auto | Out-Null
& sc.exe start $svc 2>&1 | Out-Null

Step 'Verifying public stability (10 hits must all be 200)'
Start-Sleep -Seconds 5
$fail = 0
for ($i = 1; $i -le 10; $i++) {
    try {
        $r = Invoke-WebRequest -Uri $publicUrl -TimeoutSec 10 -UseBasicParsing -MaximumRedirection 5
        $code = $r.StatusCode
    } catch { $code = $_.Exception.Response.StatusCode.value__ }
    if ($code -ne 200) { $fail++ }
    Write-Host ("  hit {0}: {1}" -f $i, $code) -ForegroundColor ($(if ($code -eq 200) { 'Green' } else { 'Red' }))
    Start-Sleep -Seconds 2
}

Step 'Result'
if ($fail -eq 0) { Write-Host "STABLE - bix.ghafservices.com is 200 on every hit (single connector -> :5000)." -ForegroundColor Green }
else { Write-Host "Still flapping ($fail/10 non-200). Check 'Get-CimInstance Win32_Service | ? PathName -match cloudflared'." -ForegroundColor Red }

Write-Host "`ncloudflared processes now:" -ForegroundColor Cyan
Get-Process cloudflared -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ("  PID {0}" -f $_.Id) }

# GhafBI - Step 3b: Install a dedicated Cloudflare Tunnel Windows service
# for bix.ghafservices.com without replacing the existing token-based
# "cloudflared" service (used for the Bix private network).
#
# Prerequisites:
#   - cloudflared installed and logged in (cert.pem present)
#   - tunnel created and DNS routed (see 3_cloudflared_config.yml)
#   - config written to C:\GhafBI\cloudflared\config.yml
#
#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$ServiceName = "GhafBITunnel"
$DisplayName = "GhafBI Cloudflare Tunnel"
$ConfigPath  = "C:\GhafBI\cloudflared\config.yml"
$Cloudflared = (Get-Command cloudflared -ErrorAction Stop).Source

if (-not (Test-Path $ConfigPath)) {
    Write-Error "Missing tunnel config: $ConfigPath"
    exit 1
}

Write-Host ""
Write-Host "=== GhafBI Tunnel Service Installer ===" -ForegroundColor Cyan

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "[OK] Old $ServiceName removed"
}

$binPath = "`"$Cloudflared`" tunnel --config `"$ConfigPath`" run"
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName "Cloudflare Tunnel for bix.ghafservices.com (GhafBI)" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host "[OK] $ServiceName is RUNNING" -ForegroundColor Green
    Write-Host "Config : $ConfigPath"
} else {
    Write-Host "[FAIL] $ServiceName status: $($svc.Status)" -ForegroundColor Red
    exit 1
}

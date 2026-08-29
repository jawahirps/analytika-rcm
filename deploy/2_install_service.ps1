# GhafBI - Step 2: Install Windows Service
# Run AS ADMINISTRATOR on the office server.
# Place this script in the same folder as Analytika.exe (the publish output).

#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

# Config - edit these if needed
$ServiceName  = "GhafBI"
$DisplayName  = "GhafBI Analytika RCM"
$AppExe       = "$PSScriptRoot\Analytika.exe"
# Live production DB + Data Protection keys live here on the office server.
$DataDir      = if ($env:GHAFBI_DATA_DIR) { $env:GHAFBI_DATA_DIR } else { "J:\GhafAnalytika\Analytika" }
$Port         = 5200
$AppUrl       = "http://localhost:$Port"

Write-Host ""
Write-Host "=== GhafBI Service Installer ===" -ForegroundColor Cyan

if (-not (Test-Path $AppExe)) {
    Write-Error "Analytika.exe not found at $AppExe"
    exit 1
}

# 1. Ensure data directory exists
New-Item -ItemType Directory -Force $DataDir | Out-Null
Write-Host "[OK] Data directory: $DataDir"

# 2. Stop and remove old service if it exists
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "[OK] Old service removed"
}

# 3. Set machine-level environment variables (persist across reboots)
[System.Environment]::SetEnvironmentVariable("DB_DIR",                 $DataDir,     "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS",        $AppUrl,      "Machine")
Write-Host "[OK] Environment variables set"

# 4. Install the service
$binPath = "`"$AppExe`" --urls $AppUrl"
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName "GhafBI Analytika RCM Portal - Healthcare Revenue Cycle Management" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Host "[OK] Service registered"

# 5. Set environment variables on the service itself via registry
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$multiSz = @(
    "DB_DIR=$DataDir",
    "ASPNETCORE_ENVIRONMENT=Production",
    "ASPNETCORE_URLS=$AppUrl"
)
New-ItemProperty -Path $regPath -Name "Environment" -Value $multiSz -PropertyType MultiString -Force | Out-Null
Write-Host "[OK] Service environment configured"

# 6. Start the service
Start-Service -Name $ServiceName
Start-Sleep -Seconds 8

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host ""
    Write-Host "=== Service is RUNNING ===" -ForegroundColor Green
    Write-Host "App URL  : $AppUrl"
    Write-Host "Data dir : $DataDir"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Open $AppUrl in a browser to verify"
    Write-Host "  2. Install cloudflared: winget install Cloudflare.cloudflared"
    Write-Host "  3. Follow 3_cloudflared_config.yml instructions"
} else {
    Write-Host ""
    Write-Host "=== Service failed to start (status: $($svc.Status)) ===" -ForegroundColor Red
    Write-Host "Check Windows Event Viewer - Windows Logs - Application for errors."
    exit 1
}

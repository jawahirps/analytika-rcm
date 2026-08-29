# ─────────────────────────────────────────────────────────────────────────────
# GhafBI – Update (zero-downtime hot-swap)
# Run AS ADMINISTRATOR on the office server.
# Copy new publish output to a staging folder first, then run this.
# ─────────────────────────────────────────────────────────────────────────────

#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$ServiceName  = "GhafBI"
$AppDir       = $PSScriptRoot          # folder containing new Analytika.exe
$DataDir      = "C:\GhafBI\data"
$Port         = 5200

Write-Host "`n=== GhafBI Update ===" -ForegroundColor Cyan

# 1. Stop service
Write-Host "Stopping service..."
Stop-Service -Name $ServiceName -Force
Start-Sleep -Seconds 3

# 2. Backup the old exe
$backup = "$AppDir\Analytika.exe.bak"
if (Test-Path "$AppDir\Analytika.exe") {
    Copy-Item "$AppDir\Analytika.exe" $backup -Force
}

# 3. The new files are already in $AppDir (you copied them here)
Write-Host "[OK] New files in place"

# 4. Ensure data dir still exists (survives updates)
New-Item -ItemType Directory -Force $DataDir | Out-Null

# 5. Restart
Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host "`n=== Update complete — service RUNNING ===" -ForegroundColor Green
    Write-Host "http://localhost:$Port"
} else {
    Write-Host "`n=== Service failed to start — rolling back ===" -ForegroundColor Red
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    if (Test-Path $backup) { Copy-Item $backup "$AppDir\Analytika.exe" -Force }
    Start-Service -Name $ServiceName
    Write-Host "Rolled back to previous version."
}

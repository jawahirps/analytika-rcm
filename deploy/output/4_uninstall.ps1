# ─────────────────────────────────────────────────────────────────────────────
# GhafBI – Uninstall / Remove Service
# Run AS ADMINISTRATOR
# ─────────────────────────────────────────────────────────────────────────────

#Requires -RunAsAdministrator
$ServiceName = "GhafBI"

Write-Host "`n=== GhafBI Uninstall ===" -ForegroundColor Yellow

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Write-Host "[OK] Service removed"
} else {
    Write-Host "[--] Service not found, nothing to remove"
}

# Remove env vars
[System.Environment]::SetEnvironmentVariable("DB_DIR",                 $null, "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", $null, "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS",        $null, "Machine")
Write-Host "[OK] Environment variables cleared"

Write-Host "`nData in C:\GhafBI\data was NOT deleted. Remove manually if needed.`n"

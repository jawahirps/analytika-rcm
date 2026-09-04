# Analytika RCM - Windows Server Deployment with Retry
# Run AS ADMINISTRATOR on the target server.

#Requires -RunAsAdministrator
param(
    [string]$TargetPath = "J:\Bix\analytika-rcm",
    [int]$MaxRetries = 3,
    [int]$RetryDelaySeconds = 15,
    [switch]$AutoFixPortConflicts = $true
)

$ErrorActionPreference = "Stop"
$ServiceName = "GhafBI"
$DisplayName = "GhafBI Analytika RCM"
$Port = 5200
$AppUrl = "http://localhost:$Port"
$DataDir = if ($env:GHAFBI_DATA_DIR) { $env:GHAFBI_DATA_DIR } else { "J:\GhafAnalytika\Analytika" }

Write-Host ""
Write-Host "=== Analytika RCM Deployment ===" -ForegroundColor Cyan
Write-Host "Target: $TargetPath" -ForegroundColor Gray
Write-Host "Service: $ServiceName" -ForegroundColor Gray
Write-Host "Port: $Port" -ForegroundColor Gray
Write-Host ""

# --- Step 1: Verify prerequisites ---
Write-Host "[1/6] Checking prerequisites..." -ForegroundColor Yellow

$AppExe = "$TargetPath\Analytika.exe"
if (-not (Test-Path $AppExe)) {
    Write-Host "  ERROR: Analytika.exe not found at $AppExe" -ForegroundColor Red
    exit 1
}
Write-Host "  [OK] Analytika.exe found" -ForegroundColor Green

New-Item -ItemType Directory -Force $DataDir | Out-Null
Write-Host "  [OK] Data directory: $DataDir" -ForegroundColor Green

# --- Step 2: Fix port conflicts ---
Write-Host ""
Write-Host "[2/6] Checking port $Port..." -ForegroundColor Yellow

$portInUse = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
if ($portInUse) {
    $pid = $portInUse.OwningProcess
    $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
    Write-Host "  Port $Port is in use by PID $pid ($($proc.ProcessName))" -ForegroundColor Yellow

    if ($AutoFixPortConflicts) {
        if ($proc.ProcessName -eq "Analytika") {
            Write-Host "  Stopping old Analytika process..." -ForegroundColor Yellow
            Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
            Write-Host "  [OK] Process stopped" -ForegroundColor Green
        } else {
            Write-Host "  WARNING: Port occupied by non-Analytika process. Cannot auto-fix." -ForegroundColor Red
            Write-Host "  Manually stop the process or change the port." -ForegroundColor Red
        }
    }
} else {
    Write-Host "  [OK] Port $Port is available" -ForegroundColor Green
}

# --- Step 3: Stop and remove old service ---
Write-Host ""
Write-Host "[3/6] Removing old service (if exists)..." -ForegroundColor Yellow

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "  Stopping $ServiceName..." -ForegroundColor Gray
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    Write-Host "  Deleting $ServiceName..." -ForegroundColor Gray
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 3
    Write-Host "  [OK] Old service removed" -ForegroundColor Green
} else {
    Write-Host "  [OK] No existing service found" -ForegroundColor Green
}

# --- Step 4: Create service with retry ---
Write-Host ""
Write-Host "[4/6] Installing service..." -ForegroundColor Yellow

$success = $false
for ($i = 1; $i -le $MaxRetries; $i++) {
    Write-Host "  Attempt $i of $MaxRetries..." -ForegroundColor Gray

    $binPath = "`"$AppExe`" --urls $AppUrl"
    $result = sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $DisplayName 2>&1
    if ($LASTEXITCODE -eq 0) {
        sc.exe description $ServiceName "GhafBI Analytika RCM Portal - Healthcare Revenue Cycle Management" | Out-Null
        sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        Write-Host "  [OK] Service registered" -ForegroundColor Green
        $success = $true
        break
    } else {
        Write-Host "  Failed: $result" -ForegroundColor Red
        if ($i -lt $MaxRetries) {
            Write-Host "  Retrying in $RetryDelaySeconds seconds..." -ForegroundColor Yellow
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
}

if (-not $success) {
    Write-Host ""
    Write-Host "  FAILED: Could not create service after $MaxRetries attempts." -ForegroundColor Red
    Write-Host "  Try running: sc.exe create $ServiceName binPath= `"$AppExe --urls $AppUrl`" start= auto DisplayName= `"$DisplayName`"" -ForegroundColor Yellow
    exit 1
}

# --- Step 5: Set environment variables in registry ---
Write-Host ""
Write-Host "[5/6] Configuring service environment..." -ForegroundColor Yellow

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
if (Test-Path $regPath) {
    $multiSz = @(
        "DB_DIR=$DataDir",
        "ASPNETCORE_ENVIRONMENT=Production",
        "ASPNETCORE_URLS=$AppUrl"
    )
    New-ItemProperty -Path $regPath -Name "Environment" -Value $multiSz -PropertyType MultiString -Force | Out-Null
    Write-Host "  [OK] Registry environment set" -ForegroundColor Green
} else {
    Write-Host "  [WARN] Registry path not found yet, setting via env vars instead" -ForegroundColor Yellow
    [System.Environment]::SetEnvironmentVariable("DB_DIR", $DataDir, "Machine")
    [System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
    [System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", $AppUrl, "Machine")
    Write-Host "  [OK] Machine environment variables set" -ForegroundColor Green
}

# --- Step 6: Start service and verify ---
Write-Host ""
Write-Host "[6/6] Starting service..." -ForegroundColor Yellow

Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 10

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  DEPLOYMENT SUCCESSFUL" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Service : $ServiceName (Running)" -ForegroundColor White
    Write-Host "  URL     : $AppUrl" -ForegroundColor White
    Write-Host "  Data    : $DataDir" -ForegroundColor White
    Write-Host ""
    Write-Host "  Verifying health..." -ForegroundColor Gray

    Start-Sleep -Seconds 5
    try {
        $health = Invoke-WebRequest -Uri "$AppUrl/" -UseBasicParsing -TimeoutSec 15
        if ($health.StatusCode -eq 200) {
            Write-Host "  [OK] Application responding (HTTP 200)" -ForegroundColor Green
        }
    } catch {
        Write-Host "  [WARN] Health check timed out (app may still be starting)" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "  Next steps:" -ForegroundColor Cyan
    Write-Host "    1. Open $AppUrl in a browser" -ForegroundColor White
    Write-Host "    2. Login: admin@ghafbi.ae / Admin@123" -ForegroundColor Yellow
    Write-Host "    3. CHANGE THE DEFAULT PASSWORD!" -ForegroundColor Red
} else {
    Write-Host ""
    Write-Host "  FAILED: Service status is $($svc.Status)" -ForegroundColor Red
    Write-Host "  Check Event Viewer > Application logs for errors." -ForegroundColor Yellow
    exit 1
}

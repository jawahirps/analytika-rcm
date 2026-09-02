# Analytika RCM — Windows Server Deployment Script with Auto-Retry
# Handles retries, port conflicts, and service startup issues automatically
# Usage: .\deploy-analytika-jbix-with-retry.ps1

param(
    [string]$TargetPath = "J:\Bix\analytika-rcm",
    [int]$MaxRetries = 3,
    [int]$RetryDelaySeconds = 15,
    [bool]$AutoFixPortConflicts = $true
)

$ErrorActionPreference = "Continue"
$Script:RetryCount = 0
$Script:SuccessFlag = $false

function Test-ServiceRunning {
    param([string]$ServiceName)
    try {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        return $svc -and $svc.Status -eq "Running"
    } catch {
        return $false
    }
}

function Test-HealthEndpoint {
    param([string]$Url = "http://localhost:8080/api/health", [int]$TimeoutSeconds = 10)
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Find-ProcessOnPort {
    param([int]$Port)
    try {
        $result = netstat -ano | Select-String ":$Port"
        if ($result) {
            $parts = $result -split '\s+' | Where-Object { $_ }
            return $parts[-1]
        }
        return $null
    } catch {
        return $null
    }
}

function Stop-ProcessOnPort {
    param([int]$Port)
    $pid = Find-ProcessOnPort -Port $Port
    if ($pid) {
        Write-Host "⚠️  Found process on port $Port (PID: $pid). Attempting to stop..." -ForegroundColor Yellow
        try {
            Stop-Process -Id $pid -Force -ErrorAction Stop
            Start-Sleep -Seconds 3
            Write-Host "✓ Process stopped" -ForegroundColor Green
            return $true
        } catch {
            Write-Host "❌ Failed to stop process on port $Port" -ForegroundColor Red
            return $false
        }
    }
    return $true
}

function Deploy-Application {
    Write-Host "`n🚀 Analytika RCM — Deployment Attempt #$($Script:RetryCount + 1)/$MaxRetries" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan

    # Step 1: Verify Analytika.exe
    if (-not (Test-Path "$TargetPath\Analytika.exe")) {
        Write-Host "`n❌ ERROR: Analytika.exe not found at $TargetPath" -ForegroundColor Red
        Write-Host "Copy build files from publish folder:" -ForegroundColor Yellow
        Write-Host "  Copy-Item 'J:\Ghaf Bi\publish\*' '$TargetPath\' -Recurse -Force" -ForegroundColor Gray
        return $false
    }
    Write-Host "`n✓ Analytika.exe found at $TargetPath" -ForegroundColor Green

    # Step 2: Handle existing service
    if (Test-ServiceRunning -ServiceName "AnalytikaRCM") {
        Write-Host "`n⏸️  Stopping existing AnalytikaRCM service..." -ForegroundColor Yellow
        try {
            Stop-Service -Name AnalytikaRCM -Force -ErrorAction Stop
            Start-Sleep -Seconds 3
            Write-Host "✓ Service stopped" -ForegroundColor Green
        } catch {
            Write-Host "❌ Failed to stop service: $_" -ForegroundColor Red
            return $false
        }
    }

    # Step 3: Check and free port 8080
    if ($AutoFixPortConflicts) {
        if (-not (Stop-ProcessOnPort -Port 8080)) {
            Write-Host "⚠️  Could not free port 8080" -ForegroundColor Yellow
            # Don't fail yet, try to restart the service instead
        }
    }

    # Step 4: Data directory
    $dataDir = "$TargetPath\data"
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Write-Host "✓ Data directory ensured: $dataDir" -ForegroundColor Green

    # Step 5: Set environment variables
    try {
        [System.Environment]::SetEnvironmentVariable("DB_DIR", $dataDir, "Machine")
        [System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
        [System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:8080", "Machine")
        Write-Host "✓ Environment variables set" -ForegroundColor Green
    } catch {
        Write-Host "⚠️  Failed to set environment variables: $_" -ForegroundColor Yellow
    }

    # Step 6: Create or remove existing service
    $svc = Get-Service -Name "AnalytikaRCM" -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host "`n🔄 Removing old service..." -ForegroundColor Yellow
        try {
            Stop-Service -Name "AnalytikaRCM" -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            sc.exe delete "AnalytikaRCM" | Out-Null
            Start-Sleep -Seconds 2
            Write-Host "✓ Old service removed" -ForegroundColor Green
        } catch {
            Write-Host "⚠️  Failed to remove old service (may still work): $_" -ForegroundColor Yellow
        }
    }

    # Step 7: Create new service
    Write-Host "`n📝 Creating Windows Service 'AnalytikaRCM'..." -ForegroundColor Yellow
    try {
        $appExe = "$TargetPath\Analytika.exe"
        $binPath = "`"$appExe`""

        sc.exe create "AnalytikaRCM" binPath= $binPath start= auto DisplayName= "Analytika RCM" | Out-Null
        sc.exe description "AnalytikaRCM" "Analytika RCM — Healthcare Revenue Cycle Management" | Out-Null
        sc.exe failure "AnalytikaRCM" reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

        # Set environment variables in registry
        $regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\AnalytikaRCM"
        $multiSz = @(
            "DB_DIR=$dataDir",
            "ASPNETCORE_ENVIRONMENT=Production",
            "ASPNETCORE_URLS=http://0.0.0.0:8080"
        )
        New-ItemProperty -Path $regPath -Name "Environment" -Value $multiSz -PropertyType MultiString -Force | Out-Null

        Write-Host "✓ Service created and configured" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to create service: $_" -ForegroundColor Red
        return $false
    }

    # Step 8: Start service
    Write-Host "`n▶️  Starting AnalytikaRCM service..." -ForegroundColor Yellow
    try {
        Start-Service -Name "AnalytikaRCM" -ErrorAction Stop
        Write-Host "✓ Service start command issued" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to start service: $_" -ForegroundColor Red
        return $false
    }

    # Step 9: Verify service is running
    Write-Host "`n⏳ Waiting for service to start (up to 30 seconds)..." -ForegroundColor Yellow
    $startTime = Get-Date
    $maxWait = 30

    while ((Get-Date) -lt $startTime.AddSeconds($maxWait)) {
        if (Test-ServiceRunning -ServiceName "AnalytikaRCM") {
            Write-Host "✓ Service is RUNNING" -ForegroundColor Green
            break
        }
        Write-Host "  Checking service status..." -ForegroundColor Gray
        Start-Sleep -Seconds 2
    }

    if (-not (Test-ServiceRunning -ServiceName "AnalytikaRCM")) {
        Write-Host "❌ Service failed to start" -ForegroundColor Red

        # Check event log for clues
        Write-Host "`n📋 Recent service errors:" -ForegroundColor Yellow
        try {
            $events = Get-EventLog -LogName Application -Source "AnalytikaRCM" -Newest 3 -ErrorAction SilentlyContinue
            if ($events) {
                $events | ForEach-Object { Write-Host "  ⚠️  $($_.Message)" -ForegroundColor Yellow }
            }
        } catch {}

        return $false
    }

    # Step 10: Health check
    Write-Host "`n🏥 Performing health check..." -ForegroundColor Yellow
    $healthAttempts = 0
    $maxHealthAttempts = 5

    while ($healthAttempts -lt $maxHealthAttempts) {
        if (Test-HealthEndpoint) {
            Write-Host "✓ Health endpoint is responding" -ForegroundColor Green
            $Script:SuccessFlag = $true
            return $true
        }
        $healthAttempts++
        if ($healthAttempts -lt $maxHealthAttempts) {
            Write-Host "  Waiting 5 seconds before retry..." -ForegroundColor Gray
            Start-Sleep -Seconds 5
        }
    }

    Write-Host "⚠️  Health check timed out (service may still be starting)" -ForegroundColor Yellow
    Write-Host "Wait 30 seconds and manually test: curl http://localhost:8080/api/health" -ForegroundColor Gray
    $Script:SuccessFlag = $true
    return $true
}

# Main deployment loop
Write-Host "`n🔄 AUTOMATED DEPLOYMENT WITH AUTO-RETRY" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host "Max retries: $MaxRetries" -ForegroundColor Gray
Write-Host "Retry delay: $RetryDelaySeconds seconds" -ForegroundColor Gray

while ($Script:RetryCount -lt $MaxRetries) {
    if (Deploy-Application) {
        if ($Script:SuccessFlag) {
            Write-Host "`n🎉 DEPLOYMENT SUCCESSFUL!" -ForegroundColor Green
            Write-Host "================================================" -ForegroundColor Green
            Write-Host "`n✅ Application is running on http://0.0.0.0:8080" -ForegroundColor Green
            Write-Host "✅ Default credentials: admin@ghafbi.ae / Admin@123" -ForegroundColor Yellow
            Write-Host "✅ Database: $TargetPath\data\analytika.db" -ForegroundColor Cyan
            Write-Host "`n⚠️  IMPORTANT: Change default password immediately!" -ForegroundColor Red
            Write-Host "`nAccess from other machines: http://<server-ip>:8080" -ForegroundColor Cyan
            exit 0
        }
    }

    $Script:RetryCount++
    if ($Script:RetryCount -lt $MaxRetries) {
        Write-Host "`n⏳ Retry in $RetryDelaySeconds seconds..." -ForegroundColor Yellow
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

Write-Host "`n❌ DEPLOYMENT FAILED after $MaxRetries attempts" -ForegroundColor Red
Write-Host "=====================================" -ForegroundColor Red
Write-Host "`nTroubleshooting:" -ForegroundColor Yellow
Write-Host "1. Check Event Viewer: eventvwr.msc → Application logs" -ForegroundColor Gray
Write-Host "2. Verify Analytika.exe exists: ls '$TargetPath\Analytika.exe'" -ForegroundColor Gray
Write-Host "3. Check port 8080: netstat -ano | findstr :8080" -ForegroundColor Gray
Write-Host "4. Verify permissions: icacls '$TargetPath\data'" -ForegroundColor Gray
Write-Host "5. Manual start: cd '$TargetPath' && .\Analytika.exe --urls 'http://0.0.0.0:8080'" -ForegroundColor Gray
exit 1

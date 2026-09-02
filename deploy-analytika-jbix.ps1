# Analytika RCM — Windows Server Deployment Script (J:\Bix)
# Run this on your Windows Server to deploy the application
# Modified to handle both GitHub downloads and local pre-copied builds

param(
    [string]$TargetPath = "J:\Bix\analytika-rcm",
    [string]$InstallService = $true
)

$ErrorActionPreference = "Stop"

Write-Host "`n🚀 Analytika RCM — Windows Deployment" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan

# Step 1: Create target directory
Write-Host "`n📁 Creating directory: $TargetPath" -ForegroundColor Yellow
if (Test-Path $TargetPath) {
    Write-Host "⚠️  Directory already exists. Backing up..." -ForegroundColor Yellow
    Rename-Item $TargetPath "$TargetPath.backup-$(Get-Date -Format 'yyyy-MM-dd-HHmmss')" -Force
}
New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null

# Step 2: Check if Analytika.exe already exists (local pre-copied build)
$analytikaMightExist = $false
if (Test-Path "$TargetPath\Analytika.exe") {
    Write-Host "`n✓ Analytika.exe already found locally. Skipping download." -ForegroundColor Green
    $analytikaMightExist = $true
}

# Step 3: Try to download from GitHub if not already present
if (-not $analytikaMightExist) {
    Write-Host "`n📥 Downloading Analytika RCM build..." -ForegroundColor Yellow

    $downloadUrl = "https://github.com/jawahirps/analytika-rcm/releases/download/latest/analytika-windows.zip"
    $zipPath = "$TargetPath\analytika-windows.zip"

    try {
        Write-Host "Downloading from GitHub..." -ForegroundColor Gray
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -Verbose -ErrorAction Stop

        # Extract build
        Write-Host "`n📦 Extracting build files..." -ForegroundColor Yellow
        Expand-Archive -Path $zipPath -DestinationPath $TargetPath -Force
        Remove-Item $zipPath -Force
        Write-Host "✓ Build extracted successfully" -ForegroundColor Green
    } catch {
        Write-Host "⚠️  GitHub download failed. Checking if build files are already present..." -ForegroundColor Yellow
        Write-Host "If you have a local build, copy Analytika.exe and DLLs to: $TargetPath" -ForegroundColor Yellow
    }
}

# Step 4: Verify Analytika.exe exists
if (-not (Test-Path "$TargetPath\Analytika.exe")) {
    Write-Host "`n❌ ERROR: Analytika.exe not found at $TargetPath" -ForegroundColor Red
    Write-Host "Please ensure the build files have been copied to: $TargetPath" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Analytika.exe found" -ForegroundColor Green

# Step 5: Download service installation script
Write-Host "`n📥 Downloading service installation script..." -ForegroundColor Yellow

$scriptUrl = "https://raw.githubusercontent.com/jawahirps/analytika-rcm/main/deploy-service-jbix.ps1"
$scriptPath = "$TargetPath\install-service.ps1"

try {
    Invoke-WebRequest -Uri $scriptUrl -OutFile $scriptPath -Verbose -ErrorAction Stop
    Write-Host "✓ Service installation script downloaded" -ForegroundColor Green
} catch {
    Write-Host "⚠️  Could not download service script. Attempting to create local version..." -ForegroundColor Yellow
    # Create a fallback service installation script inline
    $fallbackScript = @'
#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$ServiceName  = "AnalytikaRCM"
$DisplayName  = "Analytika RCM"
$AppExe       = "$PSScriptRoot\Analytika.exe"
$DataDir      = "$PSScriptRoot\data"
$Port         = 8080
$AppUrl       = "http://0.0.0.0:$Port"

Write-Host "`n=== Analytika RCM Service Installer ===" -ForegroundColor Cyan

if (-not (Test-Path $AppExe)) {
    Write-Error "Analytika.exe not found at $AppExe"
    exit 1
}

New-Item -ItemType Directory -Force $DataDir | Out-Null
Write-Host "[OK] Data directory: $DataDir"

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "[OK] Old service removed"
}

[System.Environment]::SetEnvironmentVariable("DB_DIR",                 $DataDir,     "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
[System.Environment]::SetEnvironmentVariable("ASPNETCORE_URLS",        $AppUrl,      "Machine")
Write-Host "[OK] Environment variables set"

$binPath = "`"$AppExe`""
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName "Analytika RCM — Healthcare Revenue Cycle Management" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Host "[OK] Service registered"

$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$multiSz = @(
    "DB_DIR=$DataDir",
    "ASPNETCORE_ENVIRONMENT=Production",
    "ASPNETCORE_URLS=$AppUrl"
)
New-ItemProperty -Path $regPath -Name "Environment" -Value $multiSz -PropertyType MultiString -Force | Out-Null
Write-Host "[OK] Service environment configured"

Start-Service -Name $ServiceName
Start-Sleep -Seconds 8

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host "`n=== Service is RUNNING ===" -ForegroundColor Green
    Write-Host "App URL  : http://localhost:$Port"
    Write-Host "Data dir : $DataDir"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Open http://localhost:$Port in a browser"
    Write-Host "  2. Login with: admin@ghafbi.ae / Admin@123"
    Write-Host "  3. CHANGE PASSWORD IMMEDIATELY"
} else {
    Write-Host "`n=== Service failed to start ===" -ForegroundColor Red
    exit 1
}
'@
    Set-Content -Path $scriptPath -Value $fallbackScript
    Write-Host "✓ Fallback service script created" -ForegroundColor Green
}

# Step 6: Install as Windows Service
if ($InstallService -eq $true) {
    Write-Host "`n⚙️  Installing as Windows Service..." -ForegroundColor Yellow

    if (Test-Path $scriptPath) {
        try {
            & $scriptPath
            Write-Host "✓ Service installed successfully" -ForegroundColor Green
        } catch {
            Write-Host "❌ Service installation failed. Error: $_" -ForegroundColor Red
            Write-Host "Manual: Run: $scriptPath" -ForegroundColor Yellow
            exit 1
        }
    }
}

# Step 7: Verify
Write-Host "`n✅ Verification" -ForegroundColor Cyan
Write-Host "===============" -ForegroundColor Cyan

$service = Get-Service AnalytikaRCM -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "✓ Service: $(($service).Status)" -ForegroundColor Green
    Start-Sleep -Seconds 3

    try {
        $health = Invoke-WebRequest -Uri "http://localhost:8080/api/health" -UseBasicParsing -ErrorAction SilentlyContinue
        if ($health.StatusCode -eq 200) {
            Write-Host "✓ Health check: OK" -ForegroundColor Green
            Write-Host "`n🎉 Deployment Complete!" -ForegroundColor Green
            Write-Host "Access the app at: http://<server-ip>:8080" -ForegroundColor Cyan
            Write-Host "Default login: admin@ghafbi.ae / Admin@123" -ForegroundColor Yellow
            Write-Host "⚠️  CHANGE PASSWORD IMMEDIATELY!" -ForegroundColor Red
        }
    } catch {
        Write-Host "⚠️  Health check failed (app may still be starting)" -ForegroundColor Yellow
        Write-Host "Wait 30 seconds and try: curl http://localhost:8080/api/health" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️  Service not found!" -ForegroundColor Yellow
    exit 1
}

Write-Host ""

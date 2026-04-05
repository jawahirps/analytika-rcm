# build-installer.ps1 - Build Analytika RCM Installer

# Define output directories
$publishDir = ".\Analytika\bin\Release\net10.0\publish"
$wixOutputDir = ".\wix-output"

# Step 1: Clean previous builds
Write-Host "Cleaning previous builds..."
Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $wixOutputDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $wixOutputDir | Out-Null

# Step 2: Publish .NET application
Write-Host "Publishing .NET application..."
dotnet publish Analytika/Analytika.csproj -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to publish application" -ForegroundColor Red
    exit 1
}

# Step 3: Build WiX installer
Write-Host "Building WiX installer..."
candle setup.wsx -o "$wixOutputDir\" -d "PublishDir=$publishDir"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Candle compilation failed" -ForegroundColor Red
    exit 1
}

light "$wixOutputDir\setup.wixobj" -o "$wixOutputDir\AnalytikaSetup.msi"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Light linking failed" -ForegroundColor Red
    exit 1
}

Write-Host "Installer built successfully: $wixOutputDir\AnalytikaSetup.msi" -ForegroundColor Green
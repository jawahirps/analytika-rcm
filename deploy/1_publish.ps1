# ─────────────────────────────────────────────────────────────────────────────
# GhafBI – Step 1: Publish & Package
# Run this on your DEV machine, then copy the output folder to the office server.
# ─────────────────────────────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"

$ProjectPath = "$PSScriptRoot\..\Analytika\Analytika.csproj"
$OutputPath  = "$PSScriptRoot\output"

Write-Host "`n=== GhafBI Publish ===" -ForegroundColor Cyan

# Clean previous output
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }

# Publish as self-contained Windows x64 — no .NET install needed on server
dotnet publish $ProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $OutputPath

if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed."; exit 1 }

# Create data & downloads directories
New-Item -ItemType Directory -Force "$OutputPath\data"          | Out-Null
New-Item -ItemType Directory -Force "$OutputPath\wwwroot\portal-downloads" | Out-Null

# Copy deploy scripts so the server has everything in one folder
Copy-Item "$PSScriptRoot\2_install_service.ps1" $OutputPath
Copy-Item "$PSScriptRoot\3_cloudflared_config.yml" $OutputPath
Copy-Item "$PSScriptRoot\4_uninstall.ps1" $OutputPath

Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "Output folder: $OutputPath"
Write-Host "Copy this folder to the office server, then run 2_install_service.ps1 as Administrator.`n"

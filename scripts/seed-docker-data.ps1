# seed-docker-data.ps1
# Run ONCE before the first Docker stack launch to copy the existing SQLite database
# and Data Protection keys into the Docker-managed volumes.
#
# Usage (from repo root):
#   .\scripts\seed-docker-data.ps1
#
# Safe to re-run: will not overwrite if the volume already has a database.

$ErrorActionPreference = 'Stop'

$SRC_DATA  = "J:/GhafAnalytika/Analytika"
$DB_FILE   = "$SRC_DATA/analytika.db"
$DP_DIR    = "$SRC_DATA/dataprotection-keys"

Write-Host "`n=== Analytika Docker Volume Seeder ===" -ForegroundColor Cyan

# 1. Create a temporary container attached to the target volume
Write-Host "`n[1/4] Spinning up seed container..." -ForegroundColor Yellow
$containerId = docker run -d `
    --name analytika-seeder `
    -v analytika-rcm_analytika-data:/app/data `
    alpine sh -c "sleep 300" 2>&1

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to create seed container. Are the volumes created? Run 'docker compose up --no-start' first."
    exit 1
}

try {
    # 2. Check if DB already present in volume
    $exists = docker exec analytika-seeder sh -c "test -f /app/data/analytika.db && echo yes || echo no"
    if ($exists.Trim() -eq 'yes') {
        Write-Host "[2/4] Database already present in volume — skipping copy." -ForegroundColor Green
    } else {
        # Copy DB
        Write-Host "[2/4] Copying $DB_FILE into volume..." -ForegroundColor Yellow
        docker cp "$DB_FILE" "analytika-seeder:/app/data/analytika.db"
        Write-Host "      Done." -ForegroundColor Green
    }

    # 3. Copy Data Protection keys (needed to decrypt stored credentials)
    if (Test-Path $DP_DIR) {
        Write-Host "[3/4] Copying Data Protection keys..." -ForegroundColor Yellow
        docker exec analytika-seeder mkdir -p /app/data/dataprotection-keys
        Get-ChildItem "$DP_DIR" -File | ForEach-Object {
            docker cp $_.FullName "analytika-seeder:/app/data/dataprotection-keys/$($_.Name)"
        }
        Write-Host "      Done." -ForegroundColor Green
    } else {
        Write-Host "[3/4] No Data Protection keys found at $DP_DIR — skipping." -ForegroundColor DarkYellow
    }

    # 4. Fix ownership to match the 'analytika' user in the container (UID 1000)
    Write-Host "[4/4] Setting ownership (analytika:analytika)..." -ForegroundColor Yellow
    docker exec analytika-seeder chown -R 1000:1000 /app/data
    Write-Host "      Done." -ForegroundColor Green

} finally {
    docker rm -f analytika-seeder | Out-Null
}

Write-Host "`n=== Seed complete. You can now run: docker compose up -d ===" -ForegroundColor Cyan

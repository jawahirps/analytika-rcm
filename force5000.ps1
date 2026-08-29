<#
    force5000.ps1 - run the GhafBI (port 5200) BUILD on port 5000.

    Per explicit user request, the older GhafBI build in C:\GhafBI\app is
    promoted to serve on port 5000 (the tunnel target), replacing the newer
    bix-app build. The bix-app publish dir (J:\GhafAnalytika\bix-app) is left
    untouched so this is reversible.

    Safety already applied to C:\GhafBI\app\appsettings.json (outside this
    script):
      - DefaultConnection repointed to the real 48GB prod DB
        (J:\GhafAnalytika\Analytika\analytika.db); its original worktree path
        was deleted.
      - RunDatabaseSetupOnStartup / SeedDataOnStartup / MigrateCredentialsOnStartup
        set to false so this OLDER build cannot migrate/seed/alter prod data.
      - Backup: C:\GhafBI\app\appsettings.json.bak-force5000-*

    This script only touches service wiring: it stops the current :5000 host,
    repoints the existing BixApp service at C:\GhafBI\app\Analytika.dll, and
    starts it. GhafBI's own :5200 service stays stopped+disabled.

    ASCII-only; self-elevates.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'

$BixApp    = 'BixApp'
$Dotnet    = 'C:\Program Files\dotnet\dotnet.exe'
$AppDir    = 'C:\GhafBI\app'
$Dll       = 'C:\GhafBI\app\Analytika.dll'
$Url       = 'http://localhost:5000'
$HealthUrl = 'http://127.0.0.1:5000/healthz'
$LoginUrl  = 'http://127.0.0.1:5000/login'
$PublicUrl = 'https://bix.ghafservices.com/login'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    return
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

if (-not (Test-Path $Dll)) { Write-Host "GhafBI build not found at $Dll. Aborting." -ForegroundColor Red; exit 1 }

# locate nssm
$nssmCmd = Get-Command nssm -ErrorAction SilentlyContinue
$nssm = if ($nssmCmd) { $nssmCmd.Source } else { $null }
if (-not $nssm) {
    foreach ($c in @('C:\ProgramData\chocolatey\bin\nssm.exe',
                     "$env:ProgramFiles\nssm\win64\nssm.exe",
                     "${env:ProgramFiles(x86)}\nssm\win64\nssm.exe")) {
        if (Test-Path $c) { $nssm = $c; break }
    }
}
if (-not $nssm) { Write-Host 'nssm not found.' -ForegroundColor Red; exit 1 }

# ---- 1. Stop the current :5000 host + free the DB ----
Step 'Stopping the current BixApp host (releases :5000 + the DB)'
& $nssm stop $BixApp 2>&1 | Out-Null
Start-Sleep -Seconds 2
$hosts = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Analytika.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq 0 }
foreach ($h in $hosts) {
    Write-Host ("  killing {0} PID {1}" -f $h.Name, $h.ProcessId) -ForegroundColor Yellow
    Stop-Process -Id $h.ProcessId -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

# ---- 2. Repoint the BixApp service at the GhafBI build ----
Step "Repointing '$BixApp' service -> $Dll (port 5000)"
& $nssm set $BixApp Application $Dotnet
& $nssm set $BixApp AppParameters "`"$Dll`""
& $nssm set $BixApp AppDirectory $AppDir
& $nssm set $BixApp AppEnvironmentExtra "ASPNETCORE_URLS=$Url" "ASPNETCORE_ENVIRONMENT=Production"
& $nssm set $BixApp AppStdout "$AppDir\logs\stdout.log"
& $nssm set $BixApp AppStderr "$AppDir\logs\stderr.log"
& $nssm set $BixApp Start SERVICE_AUTO_START
& $nssm set $BixApp AppExit Default Restart
& $nssm set $BixApp AppRestartDelay 5000
New-Item -ItemType Directory -Force -Path "$AppDir\logs" | Out-Null

Step "Starting $BixApp (GhafBI build)"
& $nssm start $BixApp

# ---- 3. Verify ----
Step 'Verifying local health on :5000'
$ok = $false; $elapsed = 0
for ($i = 1; $i -le 60; $i++) {
    try { $r = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 3 -UseBasicParsing; if ($r.StatusCode -eq 200) { $ok = $true; $elapsed = $i; break } } catch {}
    try { $r2 = Invoke-WebRequest -Uri $LoginUrl -TimeoutSec 3 -UseBasicParsing -MaximumRedirection 3; if ($r2.StatusCode -eq 200) { $ok = $true; $elapsed = $i; break } } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 200) { $ok = $true; $elapsed = $i; break }
    }
    Start-Sleep -Seconds 1
}
if (-not $ok) {
    Write-Host "`nApp not healthy within 60s. Check $AppDir\logs\stderr.log" -ForegroundColor Red
    Write-Host "To revert: run restore.ps1 (brings back the bix-app build on :5000)." -ForegroundColor Yellow
    exit 1
}
Write-Host "Local :5000 responded (after $elapsed s)." -ForegroundColor Green

Step 'Verifying public URL'
try {
    $p = Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 20 -UseBasicParsing -MaximumRedirection 5
    Write-Host "Public URL ($PublicUrl): $($p.StatusCode)" -ForegroundColor Green
} catch {
    Write-Host "Public URL check inconclusive (tunnel may need a moment): $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`nDone - the GhafBI (5200) build now serves on :5000." -ForegroundColor Green
Write-Host "Reverting is: restore.ps1  (re-points :5000 at the bix-app build)." -ForegroundColor Green

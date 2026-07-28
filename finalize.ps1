<#
    finalize.ps1 - one elevated pass that:
      1. Stops + DISABLES the stale GhafBITunnel connector (-> :5200) and clean-
         restarts CloudflaredBix so a single correct connector (-> :5000) serves
         the tunnel (kills the intermittent 502 flapping).
      2. Moves C:\GhafBI\app  ->  J:\GhafAnalytika\app  (consolidate off C:).
      3. Deploys the freshly-built app (Self-Diagnostics etc.) to the live
         J:\GhafAnalytika\bix-app folder and restarts BixApp on :5000.
      4. Verifies local health + public stability.

    The running site serves from J:\GhafAnalytika\bix-app (NOT C:\GhafBI\app),
    so the move does not affect uptime. Publish runs in try/finally so BixApp is
    always restarted. ASCII-only; self-elevates.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'

$oldTunnel  = 'GhafBITunnel'
$cfSvc      = 'CloudflaredBix'
$BixApp     = 'BixApp'
$Project    = 'J:\Ghaf BI\analytika-rcm\Analytika\Analytika.csproj'
$PublishDir = 'J:\GhafAnalytika\bix-app'
$MoveSrc    = 'C:\GhafBI\app'
$MoveDst    = 'J:\GhafAnalytika\app'
$HealthUrl  = 'http://127.0.0.1:5000/healthz'
$LoginUrl   = 'http://127.0.0.1:5000/login'
$PublicUrl  = 'https://bix.ghafservices.com/login'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    return
}
function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# locate nssm
$nssm = (Get-Command nssm -ErrorAction SilentlyContinue).Source
if (-not $nssm) { foreach ($c in @('C:\ProgramData\chocolatey\bin\nssm.exe',"$env:ProgramFiles\nssm\win64\nssm.exe","${env:ProgramFiles(x86)}\nssm\win64\nssm.exe")) { if (Test-Path $c) { $nssm = $c; break } } }

function Get-AppHosts {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='Analytika.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq 0 -or $_.CommandLine -like '*Analytika.dll*' }
}

# ---- 1. Kill the stale tunnel connector -------------------------------------
Step "Disabling stale $oldTunnel and restarting $cfSvc"
& sc.exe stop $oldTunnel 2>&1 | Out-Null
& sc.exe config $oldTunnel start= disabled 2>&1 | Out-Null
& sc.exe stop $cfSvc 2>&1 | Out-Null
Get-Process cloudflared -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
& sc.exe config $cfSvc start= auto | Out-Null
& sc.exe start $cfSvc 2>&1 | Out-Null

# ---- 2. Move C:\GhafBI\app -> J:\GhafAnalytika\app --------------------------
Step "Moving $MoveSrc -> $MoveDst"
if (-not (Test-Path $MoveSrc)) {
    Write-Host "  source not found (already moved?) - skipping." -ForegroundColor Yellow
} elseif (Test-Path $MoveDst) {
    Write-Host "  destination already exists - skipping move to avoid overwrite." -ForegroundColor Yellow
} else {
    try { Move-Item -Path $MoveSrc -Destination $MoveDst -Force -ErrorAction Stop; Write-Host "  moved OK." -ForegroundColor Green }
    catch { Write-Host "  move failed: $_" -ForegroundColor Red }
}

# ---- 3. Deploy the new build to the live folder ----------------------------
Step "Stopping $BixApp for publish"
& $nssm stop $BixApp 2>&1 | Out-Null
$deadline = (Get-Date).AddSeconds(20)
while (((Get-Date) -lt $deadline) -and (Get-AppHosts)) { Start-Sleep -Seconds 1 }
Get-AppHosts | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

$published = $false
try {
    Step 'Publishing (Release)'
    & dotnet publish $Project -c Release -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    $published = $true
    Write-Host 'Publish succeeded.' -ForegroundColor Green
}
catch { Write-Host "PUBLISH FAILED: $_" -ForegroundColor Red; Write-Host 'Restarting previous build...' -ForegroundColor Yellow }
finally {
    Step "Starting $BixApp"
    & sc.exe config $BixApp start= auto | Out-Null
    & $nssm start $BixApp
}

# ---- 4. Verify --------------------------------------------------------------
Step 'Verifying local health'
$ok = $false
for ($i = 1; $i -le 45; $i++) {
    try { if ((Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 3 -UseBasicParsing).StatusCode -eq 200) { $ok = $true; break } } catch {}
    Start-Sleep -Seconds 1
}
Write-Host ("  local /healthz: {0}" -f $(if ($ok) { '200 OK' } else { 'NOT healthy' })) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
try { $lc = (Invoke-WebRequest -Uri $LoginUrl -TimeoutSec 5 -UseBasicParsing -MaximumRedirection 3).StatusCode } catch { $lc = $_.Exception.Response.StatusCode.value__ }
Write-Host ("  local /login  : {0} (200 confirms NEW build)" -f $lc) -ForegroundColor Green

Step 'Verifying public stability (10 hits)'
Start-Sleep -Seconds 4
$fail = 0
for ($i = 1; $i -le 10; $i++) {
    try { $code = (Invoke-WebRequest -Uri $PublicUrl -TimeoutSec 10 -UseBasicParsing -MaximumRedirection 5).StatusCode }
    catch { $code = $_.Exception.Response.StatusCode.value__ }
    if ($code -ne 200) { $fail++ }
    Write-Host ("  hit {0}: {1}" -f $i, $code) -ForegroundColor $(if ($code -eq 200) { 'Green' } else { 'Red' })
    Start-Sleep -Seconds 2
}

Step 'Result'
if ($published -and $fail -eq 0) { Write-Host 'DONE - new build live on :5000, tunnel stable, C:\GhafBI\app moved to J:\GhafAnalytika\app.' -ForegroundColor Green }
elseif ($published) { Write-Host "New build deployed but public flapped ($fail/10). Check cloudflared services." -ForegroundColor Yellow }
else { Write-Host 'Deploy failed - old build still serving. Fix and re-run.' -ForegroundColor Red }

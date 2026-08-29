<#
    harden-tunnel.ps1 - make the Cloudflare tunnel durable (survive reboots).

    Right now bix.ghafservices.com is served by a MANUAL cloudflared connector
    (dies on reboot). The installed 'cloudflared' Windows service points at a
    dead token tunnel (da8a96d0) and is disabled. This script repoints the
    service at the WORKING named tunnel (cc5c75c4) via a config in the system
    profile, enables it auto-start, and starts it - then removes the manual
    connectors so only the service runs.

    SAFETY: if the reinstalled service does not serve the site, the script
    starts a manual connector again so the site is never left down.

    ASCII-only; self-elevates.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Continue'

$cf         = 'C:\Program Files (x86)\cloudflared\cloudflared.exe'
$tunnelId   = 'cc5c75c4-a585-4002-b643-75db7e662122'
$userDir    = 'C:\Users\jawah\.cloudflared'
$sysDir     = 'C:\Windows\System32\config\systemprofile\.cloudflared'
$sysConfig  = "$sysDir\config.yml"
$publicUrl  = 'https://bix.ghafservices.com/login'
$logDir     = 'J:\GhafAnalytika\bix-app\logs'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevation required - relaunching as Administrator...' -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
    return
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Test-Public {
    for ($i = 1; $i -le 15; $i++) {
        try { $r = Invoke-WebRequest -Uri $publicUrl -TimeoutSec 8 -UseBasicParsing -MaximumRedirection 5
              if ($r.StatusCode -eq 200) { return $true } } catch {}
        Start-Sleep -Seconds 3
    }
    return $false
}
function Start-ManualConnector {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    Start-Process -FilePath $cf `
        -ArgumentList @('tunnel','--config',"`"$sysConfig`"",'run') `
        -WindowStyle Hidden `
        -RedirectStandardOutput "$logDir\cloudflared.out.log" `
        -RedirectStandardError  "$logDir\cloudflared.err.log"
}

if (-not (Test-Path $cf)) { $alt=(Get-Command cloudflared -EA SilentlyContinue).Source; if($alt){$cf=$alt}else{Write-Host 'cloudflared not found';exit 1} }

# ---- 1. Stage config + credentials into the system profile ----
Step 'Staging tunnel config into the LocalSystem profile'
New-Item -ItemType Directory -Force -Path $sysDir | Out-Null
Copy-Item "$userDir\$tunnelId.json" $sysDir -Force
if (Test-Path "$userDir\cert.pem") { Copy-Item "$userDir\cert.pem" $sysDir -Force }
@"
tunnel: $tunnelId
credentials-file: $sysDir\$tunnelId.json

ingress:
  - hostname: bix.ghafservices.com
    service: http://localhost:5000
  - service: http_status:404
"@ | Set-Content -Path $sysConfig -Encoding ASCII
Write-Host "Wrote $sysConfig (-> localhost:5000)" -ForegroundColor Green

# ---- Locate nssm (reliable service arg handling, unlike bare sc/config) ----
$nssmCmd = Get-Command nssm -ErrorAction SilentlyContinue
$nssm = if ($nssmCmd) { $nssmCmd.Source } else { $null }
if (-not $nssm) {
    foreach ($c in @('C:\ProgramData\chocolatey\bin\nssm.exe',
                     "$env:ProgramFiles\nssm\win64\nssm.exe",
                     "${env:ProgramFiles(x86)}\nssm\win64\nssm.exe")) {
        if (Test-Path $c) { $nssm = $c; break }
    }
}
if (-not $nssm) { Write-Host 'nssm not found - cannot install a durable service.' -ForegroundColor Red; exit 1 }

$svcName = 'CloudflaredBix'

# ---- 2. Remove the broken cloudflared service + stop all connectors ----
Step 'Removing the broken cloudflared service + stopping connectors'
& $cf service uninstall 2>&1 | Out-Null
& sc.exe stop cloudflared 2>&1 | Out-Null
& sc.exe delete cloudflared 2>&1 | Out-Null
& $nssm stop $svcName 2>&1 | Out-Null
& $nssm remove $svcName confirm 2>&1 | Out-Null
Start-Sleep -Seconds 2
Get-Process cloudflared -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

# ---- 3. Install a durable NSSM-managed cloudflared service ----
Step "Installing durable service '$svcName' (named tunnel cc5c75c4 -> :5000)"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
& $nssm install $svcName $cf
& $nssm set $svcName AppParameters "tunnel --config `"$sysConfig`" run"
& $nssm set $svcName Start SERVICE_AUTO_START
& $nssm set $svcName AppStdout "$logDir\cloudflared.out.log"
& $nssm set $svcName AppStderr "$logDir\cloudflared.err.log"
& $nssm set $svcName AppExit Default Restart
& $nssm set $svcName AppRestartDelay 5000
& $nssm start $svcName

# ---- 4. Verify; fall back to a manual connector if the service fails ----
Step 'Verifying public URL (service connector)'
Start-Sleep -Seconds 3
if (Test-Public) {
    Write-Host "`nTunnel hardened: bix.ghafservices.com is live via the auto-start '$svcName' SERVICE." -ForegroundColor Green
    Write-Host "It will now come back automatically after a reboot." -ForegroundColor Green
    # make sure no duplicate manual connector lingers (keep only the service's)
    $svcPid = (Get-CimInstance Win32_Service -Filter "Name='$svcName'").ProcessId
    Get-Process cloudflared -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $svcPid } | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Write-Host ("Service state: {0}, PID {1}" -f (Get-CimInstance Win32_Service -Filter "Name='$svcName'").State, $svcPid) -ForegroundColor Green
} else {
    Write-Host "`nService connector did not serve the site - starting a manual connector as fallback..." -ForegroundColor Yellow
    Start-ManualConnector
    if (Test-Public) {
        Write-Host 'Site is back up via a MANUAL connector (NOT durable across reboot).' -ForegroundColor Yellow
        Write-Host "Check why the service failed: $logDir\cloudflared.err.log and 'sc query cloudflared'." -ForegroundColor Yellow
    } else {
        Write-Host 'SITE STILL DOWN. Check cloudflared logs and that BixApp is listening on :5000.' -ForegroundColor Red
        exit 1
    }
}

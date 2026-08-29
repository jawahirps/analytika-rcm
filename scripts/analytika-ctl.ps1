<#
.SYNOPSIS
    Analytika server controller — Windows
.DESCRIPTION
    Controls the Analytika ASP.NET Core server process on Windows.

    Commands
    --------
    run      Start the full web server (port 5000, background jobs OFF)
    bgrun    Background-only mode: fetch & save jobs, no public HTTP port
    pause    Freeze the process in memory (keeps port held, zero CPU)
    resume   Unfreeze a paused process
    restart  Stop then start (same mode as last run)
    stop     Gracefully stop the server (or force after 10 s)
    status   Show running state, PID, uptime, and port
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('run','bgrun','pause','resume','restart','stop','status')]
    [string]$Action = 'status'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Paths ────────────────────────────────────────────────────────────────────
$RepoRoot  = Split-Path $PSScriptRoot -Parent
$AppDir    = Join-Path $RepoRoot 'Analytika'
$RunDir    = Join-Path $RepoRoot '.run'
$WebPid    = Join-Path $RunDir 'web.pid'
$BgPid     = Join-Path $RunDir 'worker.pid'
$ModFile   = Join-Path $RunDir 'mode'
$WebLog    = Join-Path $RunDir 'web.log'
$BgLog     = Join-Path $RunDir 'worker.log'

# ── Colours ──────────────────────────────────────────────────────────────────
function Write-Ok($msg)   { Write-Host "  [OK]  $msg" -ForegroundColor Green  }
function Write-Err($msg)  { Write-Host " [FAIL] $msg" -ForegroundColor Red    }
function Write-Info($msg) { Write-Host "  [--]  $msg" -ForegroundColor Cyan   }
function Write-Warn($msg) { Write-Host "  [!!]  $msg" -ForegroundColor Yellow }

# ── NtSuspend/NtResume via P/Invoke ─────────────────────────────────────────
function Load-Suspender {
    if (-not ('NtCtl' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NtCtl {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
}
'@
    }
}

# ── .env loader ──────────────────────────────────────────────────────────────
function Import-DotEnv {
    $envFile = Join-Path $RepoRoot '.env'
    if (-not (Test-Path $envFile)) { return }
    Get-Content $envFile | Where-Object { $_ -match '^[A-Za-z_]' -and $_ -notmatch '^\s*#' } | ForEach-Object {
        $k, $v = $_ -split '=', 2
        if ($k -and $v -and -not [Environment]::GetEnvironmentVariable($k.Trim())) {
            [Environment]::SetEnvironmentVariable($k.Trim(), $v.Trim(), 'Process')
        }
    }
}

# ── PID helpers ──────────────────────────────────────────────────────────────
function Get-SavedPid($file) {
    if (-not (Test-Path $file)) { return $null }
    $id = [int](Get-Content $file -Raw).Trim()
    try { Get-Process -Id $id -ErrorAction Stop | Out-Null; return $id } catch { Remove-Item $file -Force; return $null }
}

function Save-Pid($file, $id) {
    if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory $RunDir | Out-Null }
    Set-Content $file $id
}

function Remove-PidFile($file) { if (Test-Path $file) { Remove-Item $file -Force } }

# ── Process launcher ─────────────────────────────────────────────────────────
function Start-Analytika($mode) {
    Import-DotEnv
    if (-not (Test-Path $RunDir)) { New-Item -ItemType Directory $RunDir | Out-Null }

    $dbDir = if ($env:DB_DIR) { $env:DB_DIR } else { 'J:/GhafAnalytika/Analytika' }

    $common = @(
        "DB_DIR=$dbDir"
        "ASPNETCORE_ENVIRONMENT=Production"
        "StartupMaintenance__RunDatabaseSetupOnStartup=false"
        "StartupMaintenance__CreateIndexesOnStartup=false"
        "StartupMaintenance__SeedDataOnStartup=false"
    )

    if ($mode -eq 'web') {
        $envVars = $common + @(
            "ASPNETCORE_URLS=http://+:5000"
            "BackgroundJobs__HangfireServerEnabled=false"
            "BackgroundJobs__RecurringJobsEnabled=false"
            "BackgroundJobs__HangfireDashboardEnabled=false"
            "BackgroundJobs__PendingDownloads__HostedServiceEnabled=false"
        )
        $logFile = $WebLog
        $pidFile = $WebPid
    } else {
        # bgrun — loopback only, all fetch/save jobs enabled
        $envVars = $common + @(
            "ASPNETCORE_URLS=http://127.0.0.1:8081"
            "StartupMaintenance__RunDatabaseSetupOnStartup=true"
            "StartupMaintenance__CreateIndexesOnStartup=true"
            "BackgroundJobs__HangfireServerEnabled=true"
            "BackgroundJobs__RecurringJobsEnabled=true"
            "BackgroundJobs__HangfireDashboardEnabled=false"
            "BackgroundJobs__PendingDownloads__HostedServiceEnabled=true"
            "BackgroundJobs__PendingDownloads__ScheduledRunEnabled=true"
            "BackgroundJobs__PendingDownloads__ScheduledLocalTime=02:30"
            "BackgroundJobs__PendingDownloads__AutoParseRemittance=true"
            "BackgroundJobs__PendingDownloads__BatchSize=25"
        )
        $logFile = $BgLog
        $pidFile = $BgPid
    }

    # Build the env block for the child process
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName        = 'dotnet'
    $psi.Arguments       = 'run --no-build'
    $psi.WorkingDirectory = $AppDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow  = $true

    foreach ($kv in $envVars) {
        $k, $v = $kv -split '=', 2
        $psi.EnvironmentVariables[$k] = $v
    }
    if ($env:ANTHROPIC_API_KEY) { $psi.EnvironmentVariables['Anthropic__ApiKey'] = $env:ANTHROPIC_API_KEY }

    $proc = [System.Diagnostics.Process]::Start($psi)
    Save-Pid $pidFile $proc.Id
    Set-Content $ModFile $mode

    # Async log capture
    $null = $proc.BeginOutputReadLine()
    $null = $proc.BeginErrorReadLine()
    Register-ObjectEvent $proc 'OutputDataReceived' -Action { if ($Event.SourceEventArgs.Data) { Add-Content $using:logFile $Event.SourceEventArgs.Data } } | Out-Null
    Register-ObjectEvent $proc 'ErrorDataReceived'  -Action { if ($Event.SourceEventArgs.Data) { Add-Content $using:logFile $Event.SourceEventArgs.Data } } | Out-Null

    return $proc.Id
}

# ── ACTION: status ────────────────────────────────────────────────────────────
function Invoke-Status {
    Write-Host "`n  Analytika Server Status" -ForegroundColor White
    Write-Host "  ────────────────────────────────────────" -ForegroundColor DarkGray

    $webId = Get-SavedPid $WebPid
    $bgId  = Get-SavedPid $BgPid

    if ($webId) {
        $proc    = Get-Process -Id $webId
        $uptime  = (Get-Date) - $proc.StartTime
        $uptimeStr = '{0:D2}h {1:D2}m {2:D2}s' -f [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds
        $listen  = netstat -ano 2>$null | Select-String ":5000\s.*LISTEN.*$webId" | Select-Object -First 1
        Write-Ok  "WEB SERVER  PID $webId  |  up $uptimeStr  |  CPU $([math]::Round($proc.CPU,1))s"
        if ($listen) { Write-Info "            Listening on http://+:5000" }
        else          { Write-Warn "            Port 5000 not detected (starting up?)" }
    } else {
        Write-Warn "WEB SERVER  not running"
    }

    if ($bgId) {
        $proc    = Get-Process -Id $bgId
        $uptime  = (Get-Date) - $proc.StartTime
        $uptimeStr = '{0:D2}h {1:D2}m {2:D2}s' -f [int]$uptime.TotalHours, $uptime.Minutes, $uptime.Seconds
        Write-Ok  "BG WORKER   PID $bgId   |  up $uptimeStr  |  CPU $([math]::Round($proc.CPU,1))s"
        Write-Info "            Loopback only — fetch & save jobs active"
    } else {
        Write-Info "BG WORKER   not running"
    }

    Write-Host ""
}

# ── ACTION: run ───────────────────────────────────────────────────────────────
function Invoke-Run {
    if (Get-SavedPid $WebPid) { Write-Warn "Web server already running. Use 'restart' to reload."; return }
    Write-Info "Starting web server on http://+:5000 ..."
    $id = Start-Analytika 'web'
    Start-Sleep 8
    $ok = Invoke-WebRequest "http://localhost:5000/healthz" -UseBasicParsing -TimeoutSec 5 -ErrorAction SilentlyContinue
    if ($ok -and $ok.StatusCode -in 200,503) {
        Write-Ok "Web server running  PID $id  →  http://localhost:5000"
    } else {
        Write-Warn "Server started (PID $id) but /healthz not yet responding — give it a few more seconds."
        Write-Info "Tail logs:  Get-Content $WebLog -Wait -Tail 20"
    }
}

# ── ACTION: bgrun ─────────────────────────────────────────────────────────────
function Invoke-BgRun {
    if (Get-SavedPid $BgPid) { Write-Warn "Background worker already running. Use 'stop' first."; return }
    Write-Info "Starting background worker (fetch & save only, loopback 8081) ..."
    $id = Start-Analytika 'worker'
    Start-Sleep 6
    if (Get-SavedPid $BgPid) {
        Write-Ok "Background worker running  PID $id"
        Write-Info "Jobs: dha-daily-sync  remittance-auto-parse  db-nightly-backup  data-retention"
        Write-Info "Tail logs:  Get-Content $BgLog -Wait -Tail 20"
    } else {
        Write-Err "Worker failed to stay up. Check:  Get-Content $BgLog -Tail 30"
    }
}

# ── ACTION: pause ─────────────────────────────────────────────────────────────
function Invoke-Pause {
    Load-Suspender
    $webId = Get-SavedPid $WebPid
    $bgId  = Get-SavedPid $BgPid
    $any   = $false
    foreach ($id in @($webId, $bgId) | Where-Object { $_ }) {
        $h = (Get-Process -Id $id).Handle
        [NtCtl]::NtSuspendProcess($h) | Out-Null
        Write-Ok "Paused PID $id (frozen in memory, zero CPU)"
        $any = $true
    }
    if (-not $any) { Write-Warn "No running process to pause." }
}

# ── ACTION: resume ────────────────────────────────────────────────────────────
function Invoke-Resume {
    Load-Suspender
    $webId = Get-SavedPid $WebPid
    $bgId  = Get-SavedPid $BgPid
    $any   = $false
    foreach ($id in @($webId, $bgId) | Where-Object { $_ }) {
        $h = (Get-Process -Id $id).Handle
        [NtCtl]::NtResumeProcess($h) | Out-Null
        Write-Ok "Resumed PID $id"
        $any = $true
    }
    if (-not $any) { Write-Warn "No paused process found." }
}

# ── ACTION: stop ──────────────────────────────────────────────────────────────
function Invoke-Stop {
    $webId = Get-SavedPid $WebPid
    $bgId  = Get-SavedPid $BgPid
    $any   = $false
    foreach ($pair in @(@($webId,$WebPid), @($bgId,$BgPid))) {
        $id, $pf = $pair
        if (-not $id) { continue }
        $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($proc) {
            $proc.CloseMainWindow() | Out-Null
            if (-not $proc.WaitForExit(10000)) { $proc.Kill() }
            Write-Ok "Stopped PID $id"
        }
        Remove-PidFile $pf
        $any = $true
    }
    if (-not $any) { Write-Info "No running process to stop." }
}

# ── ACTION: restart ───────────────────────────────────────────────────────────
function Invoke-Restart {
    $mode = if (Test-Path $ModFile) { (Get-Content $ModFile -Raw).Trim() } else { 'web' }
    Write-Info "Restarting in '$mode' mode ..."
    Invoke-Stop
    Start-Sleep 2
    if ($mode -eq 'worker') { Invoke-BgRun } else { Invoke-Run }
}

# ── Dispatch ──────────────────────────────────────────────────────────────────
switch ($Action) {
    'run'     { Invoke-Run     }
    'bgrun'   { Invoke-BgRun   }
    'pause'   { Invoke-Pause   }
    'resume'  { Invoke-Resume  }
    'restart' { Invoke-Restart }
    'stop'    { Invoke-Stop    }
    'status'  { Invoke-Status  }
}

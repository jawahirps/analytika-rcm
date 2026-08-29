param(
    [string]$Url = "http://localhost:5000",
    [string]$Project = "Analytika\Analytika.csproj"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $Project
$logPath = Join-Path $repoRoot ".codex-run.log"
$errPath = Join-Path $repoRoot ".codex-run.err.log"

if (-not (Test-Path $projectPath)) {
    throw "Project not found: $projectPath"
}

$existing = netstat -ano -p tcp |
    Select-String -Pattern "(127\.0\.0\.1:5000|0\.0\.0\.0:5000|\[::1\]:5000|\[::\]:5000)\s" |
    Where-Object { $_.Line -match "\sLISTENING\s" }
if ($existing) {
    & (Join-Path $PSScriptRoot "qa-startup-check.ps1") -BaseUrl $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Startup QA failed for the existing app on port 5000."
    }
    Start-Process $Url
    Write-Host "Existing app on port 5000 passed startup QA." -ForegroundColor Green
    return
}

$appArgs = "run --project `"$projectPath`" --no-launch-profile -- --urls $Url"
$process = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList $appArgs `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $logPath `
    -RedirectStandardError $errPath `
    -PassThru `
    -WindowStyle Hidden

try {
    $ready = $false
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        if ($process.HasExited) {
            throw "App exited during startup. See $errPath and $logPath."
        }

        try {
            $response = Invoke-WebRequest -Uri "$Url/Home/Dashboard" -UseBasicParsing -TimeoutSec 2
            if ([int]$response.StatusCode -lt 500) {
                $ready = $true
                break
            }
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }

    if (-not $ready) {
        throw "App did not become reachable at $Url within 45 seconds."
    }

    & (Join-Path $PSScriptRoot "qa-startup-check.ps1") -BaseUrl $Url
    if ($LASTEXITCODE -ne 0) {
        throw "Startup QA failed."
    }

    Start-Process $Url
    Write-Host "Verified app is running at $Url (PID $($process.Id))." -ForegroundColor Green
    Wait-Process -Id $process.Id
} catch {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    throw
}

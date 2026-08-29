param(
    [string]$BaseUrl = "http://127.0.0.1:5000",
    [int]$TimeoutSec = 15
)

$ErrorActionPreference = "Stop"

function Test-Url {
    param([string]$Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec
        [pscustomobject]@{
            Url = $Url
            Ok = $true
            Status = [int]$response.StatusCode
            Length = $response.RawContentLength
            Error = ""
        }
    } catch {
        [pscustomobject]@{
            Url = $Url
            Ok = $false
            Status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null }
            Length = 0
            Error = $_.Exception.Message
        }
    }
}

$checks = @(
    "$BaseUrl/Home/Dashboard",
    "$BaseUrl/Home/RCMDashboard",
    "$BaseUrl/Portal/Sync",
    "$BaseUrl/Admin/Credentials",
    "$BaseUrl/ReportScheduler/ClaimSummaryReport",
    "$BaseUrl/Resubmission/Workload",
    "$BaseUrl/Admin/Users"
)

$results = $checks | ForEach-Object { Test-Url $_ }
$failed = @($results | Where-Object { -not $_.Ok -or ($_.Status -ge 500) })

$results | Format-Table -AutoSize

if ($failed.Count -gt 0) {
    Write-Error ("Startup QA failed for {0} endpoint(s)." -f $failed.Count)
    exit 1
}

Write-Host "Startup QA passed." -ForegroundColor Green

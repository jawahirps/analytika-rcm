$job = Start-Job -ScriptBlock { & "J:/GhafAnalytika/analytika-rcm/watchtower.ps1" }
$job.Id | Set-Content "J:/GhafAnalytika/analytika-rcm/watchtower.pid"
Write-Host "Watchtower started as job $($job.Id)"

$repoDir   = "J:/GhafAnalytika/analytika-rcm"
$projectFile = "J:/GhafAnalytika/analytika-rcm/Analytika/Analytika.csproj"
$logFile   = "J:/GhafAnalytika/analytika-rcm/watchtower.log"
$dbDir     = "J:/GhafAnalytika/Analytika"
$branch    = "main"
$remote    = "origin"

function Write-Log {
    param([string]$Message)
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $Message"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

Write-Log "Watchtower started. Repo: $repoDir  Branch: $branch"

while ($true) {
    try {
        Set-Location $repoDir

        # 1. Fetch
        git fetch $remote 2>&1 | Out-Null

        # 2. Compare HEAD vs origin/main
        $local  = git rev-parse HEAD 2>&1
        $remote_sha = git rev-parse "$remote/$branch" 2>&1

        if ($local -ne $remote_sha) {
            Write-Log "New commit detected. Local=$local  Remote=$remote_sha"

            # a. Pull
            $pullOut = git pull $remote $branch 2>&1
            Write-Log "git pull: $pullOut"

            # b. Build first — only stop the running app if the build succeeds
            Write-Log "Starting dotnet build..."
            $buildOut = dotnet build "$projectFile" -c Debug 2>&1
            $buildSuccess = $LASTEXITCODE -eq 0

            if ($buildSuccess) {
                Write-Log "Build succeeded."
                # c. Stop existing process now that we know the new build is good
                Stop-Process -Name Analytika -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 3
                Write-Log "Stopped existing Analytika process (if any)."
            } else {
                Write-Log "Build FAILED — keeping existing process running. Output:`n$($buildOut -join "`n")"
            }

            # d. Start new process if build succeeded
            if ($buildSuccess) {
                $psi = [System.Diagnostics.ProcessStartInfo]::new()
                $psi.FileName  = "dotnet"
                $psi.Arguments = "run --no-build --project `"$projectFile`" --urls http://localhost:5000"
                $psi.UseShellExecute = $false
                $psi.EnvironmentVariables["DB_DIR"] = $dbDir

                $proc = [System.Diagnostics.Process]::Start($psi)

                # e. Log new PID
                Write-Log "Started Analytika with PID $($proc.Id)."
            }
        } else {
            Write-Log "No new commits. HEAD=$local"
        }
    } catch {
        Write-Log "ERROR: $_"
    }

    Start-Sleep -Seconds 300
}

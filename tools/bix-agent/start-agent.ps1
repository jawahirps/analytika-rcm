<#
    start-agent.ps1 - run the Bix remote agent (Claude Agent SDK over HTTP).

    Serves http://127.0.0.1:5178/agent, published by the Cloudflare tunnel at
    https://dev-bix.ghafservices.com/agent behind a Cloudflare Access policy.

    The access token is generated on first run into agent-token.txt.

    Usage:
      .\start-agent.ps1          # start detached
      .\start-agent.ps1 -Stop    # stop it
#>
[CmdletBinding()]
param([switch]$Stop)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Log  = Join-Path $Here 'agent.log'

function Get-AgentProcess {
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*bix-agent*server.mjs*' }
}

if ($Stop) {
    $p = Get-AgentProcess
    if ($p) { $p | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; 'Agent stopped.' }
    else    { 'Agent was not running.' }
    return
}

$existing = Get-AgentProcess
if ($existing) { $existing | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Start-Sleep -Seconds 1 }

Start-Process -FilePath 'node' -ArgumentList 'server.mjs' `
              -WorkingDirectory $Here `
              -RedirectStandardOutput $Log `
              -RedirectStandardError (Join-Path $Here 'agent.err.log') `
              -WindowStyle Hidden

for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-WebRequest 'http://127.0.0.1:5178/agent/api/health' -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) {
            Write-Host "Agent live at http://127.0.0.1:5178/agent" -ForegroundColor Green
            Write-Host "token: $(Join-Path $Here 'agent-token.txt')" -ForegroundColor Gray
            return
        }
    } catch { }
}
Write-Host "Agent did not answer - check $Log" -ForegroundColor Red
exit 1

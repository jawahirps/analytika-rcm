$ErrorActionPreference = 'Stop'

$workerRoot = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '..\Analytika.dll')) {
    $workerRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
}

$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:DB_DIR = 'J:\GhafAnalytika\bix-dev-data'

Set-Location -LiteralPath $workerRoot
& dotnet (Join-Path $workerRoot 'Analytika.dll') --report-worker
exit $LASTEXITCODE

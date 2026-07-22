<#
    setup-autodeploy.ps1 - RUN ONCE, ELEVATED (right-click > Run as Administrator,
    or from an admin PowerShell). This is the ONLY time you need to click UAC.

    It grants YOUR user account permission to stop/start the BixApp service and
    write to the publish folder, so that deploy-core.ps1 (and Claude) can deploy
    the app silently from then on - no more UAC prompts.

    ASCII-only on purpose (Windows PowerShell 5.1 parsing).
#>
[CmdletBinding()]
param(
    [string]$User = "$env:USERDOMAIN\$env:USERNAME",
    [switch]$DeployNow
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'This setup must run elevated. Relaunching as Administrator...' -ForegroundColor Yellow
    $args2 = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-User',"`"$User`"")
    if ($DeployNow) { $args2 += '-DeployNow' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $args2
    return
}

$ServiceName = 'BixApp'
$PublishDir  = 'J:\GhafAnalytika\bix-app'

Write-Host "Granting '$User' service-control rights on $ServiceName ..." -ForegroundColor Cyan

# Resolve the user's SID
$sid = (New-Object System.Security.Principal.NTAccount($User)
       ).Translate([System.Security.Principal.SecurityIdentifier]).Value

# Current service security descriptor (SDDL)
$sddl = ((& sc.exe sdshow $ServiceName) | Where-Object { $_ -match 'D:' }) -join ''
if ([string]::IsNullOrWhiteSpace($sddl)) { throw "Could not read SDDL for $ServiceName" }

# ACE granting query/start/stop/pause/interrogate/read: CC LC SW RP WP DT LO CR RC
$ace = "(A;;CCLCSWRPWPDTLOCRRC;;;$sid)"

if ($sddl -like "*$sid*") {
    Write-Host 'User already has an ACE on the service - leaving as is.' -ForegroundColor Yellow
} else {
    # Insert the ACE at the front of the DACL (right after 'D:' and any flags)
    $newSddl = [regex]::Replace($sddl, '(D:(?:[A-Z]*))', "`$1$ace", 1)
    & sc.exe sdset $ServiceName "$newSddl" | Out-Host
    Write-Host 'Service rights granted.' -ForegroundColor Green
}

# Ensure the user can write the publish folder (for dotnet publish)
if (Test-Path $PublishDir) {
    Write-Host "Granting modify rights on $PublishDir ..." -ForegroundColor Cyan
    & icacls "$PublishDir" /grant "${User}:(OI)(CI)M" /T /C | Out-Null
    Write-Host 'Publish-folder rights granted.' -ForegroundColor Green
}

Write-Host "`nSetup complete. From now on, deploys run WITHOUT elevation:" -ForegroundColor Green
Write-Host "    powershell -ExecutionPolicy Bypass -File 'J:\Ghaf BI\analytika-rcm\deploy-core.ps1' -SkipHealthCheck" -ForegroundColor Gray

if ($DeployNow) {
    Write-Host "`nRunning an initial deploy now..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'J:\Ghaf BI\analytika-rcm\deploy-core.ps1' -SkipHealthCheck
}

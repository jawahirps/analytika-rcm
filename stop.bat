@echo off
echo Stopping Analytika RCM...
taskkill /im cloudflared.exe /f >nul 2>&1
taskkill /im dotnet.exe /f >nul 2>&1
taskkill /fi "windowtitle eq Analytika-App" /f >nul 2>&1
echo Done.

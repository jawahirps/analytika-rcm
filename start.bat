@echo off
title Analytika RCM - Local Server
color 0A
echo.
echo  ============================================
echo   Analytika RCM - One-Click Launcher
echo  ============================================
echo.

:: Check if dotnet is installed
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo  [ERROR] .NET SDK not found. Install it:
    echo          winget install Microsoft.DotNet.SDK.10
    echo.
    pause
    exit /b 1
)

:: Check if cloudflared is installed
where cloudflared >nul 2>&1
if %errorlevel% neq 0 (
    echo  [WARNING] cloudflared not found - app will run locally only.
    echo            Install it: winget install Cloudflare.cloudflared
    echo.
    set NO_TUNNEL=1
)

:: Find project file
set PROJECT=%~dp0Analytika\Analytika.csproj
if not exist "%PROJECT%" (
    echo  [ERROR] Analytika.csproj not found at %PROJECT%
    pause
    exit /b 1
)

echo  [1/2] Starting Analytika RCM on http://localhost:5200 ...
echo.

:: Start the app in background
start /min "Analytika-App" cmd /c "dotnet run --project "%PROJECT%" --urls http://localhost:5200"

:: Wait for app to be ready
echo  Waiting for app to start...
:wait_loop
timeout /t 2 /nobreak >nul
curl -s -o nul -w "%%{http_code}" http://localhost:5200/ 2>nul | findstr "200" >nul
if %errorlevel% neq 0 goto wait_loop

echo  [OK] App is running at http://localhost:5200
echo.

:: Start Cloudflare tunnel
if defined NO_TUNNEL (
    echo  App running at: http://localhost:5200
    echo  No public URL - install cloudflared for public access.
    echo.
    echo  Press any key to stop the server...
    pause >nul
) else (
    echo  [2/2] Starting Cloudflare Tunnel...
    echo.
    echo  ============================================
    echo   Look for the public URL below:
    echo   https://xxxxx.trycloudflare.com
    echo  ============================================
    echo.
    cloudflared tunnel --url http://localhost:5200
)

:: Cleanup - kill the app when tunnel closes
taskkill /fi "windowtitle eq Analytika-App" /f >nul 2>&1
echo.
echo  Server stopped.

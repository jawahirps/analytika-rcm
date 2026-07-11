@echo off
set DB_DIR=J:\GhafAnalytika\Analytika
start /b cmd /c "timeout /t 3 >nul && start http://localhost:5000"
dotnet run --project Analytika\Analytika.csproj --no-launch-profile

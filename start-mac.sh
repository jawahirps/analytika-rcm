#!/bin/bash
export DB_DIR="$HOME/GhafAnalytika/Analytika"
(sleep 3 && open http://localhost:5000) &
dotnet run --project Analytika/Analytika.csproj --no-launch-profile

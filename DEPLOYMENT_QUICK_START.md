# Analytika RCM — Quick Deployment Guide

**TL;DR — Deploy in 3 steps:**

## Step 1: Build & Copy (5 minutes)

```powershell
# Build the application
cd "C:\path\to\analytika-rcm"
dotnet publish -c Release -o "J:\Ghaf Bi\publish"

# Copy to deployment location
mkdir "J:\Bix\analytika-rcm"
Copy-Item "J:\Ghaf Bi\publish\*" "J:\Bix\analytika-rcm\" -Recurse -Force
```

## Step 2: Deploy (2 minutes)

**Run as Administrator:**

```powershell
cd "J:\Bix"
.\deploy-analytika-jbix.ps1 -TargetPath "J:\Bix\analytika-rcm" -InstallService $true
```

## Step 3: Access (1 minute)

1. Open `http://localhost:8080` in a browser
2. Login: `admin@ghafbi.ae` / `Admin@123`
3. **Change password immediately**

---

## Verify It's Running

```powershell
# Check service status
Get-Service AnalytikaRCM

# Test health endpoint
curl http://localhost:8080/api/health
```

---

## Common Tasks

### Restart the Service
```powershell
Restart-Service AnalytikaRCM
```

### Stop the Service
```powershell
Stop-Service AnalytikaRCM
```

### View Logs
```powershell
Get-EventLog -LogName Application -Source "AnalytikaRCM" -Newest 10
```

### Backup Database
```powershell
Stop-Service AnalytikaRCM
Copy-Item "J:\Bix\analytika-rcm\data\analytika.db" "J:\Bix\analytika-rcm\backups\analytika-$(Get-Date -Format 'yyyy-MM-dd').db"
Start-Service AnalytikaRCM
```

### Change Port (from 8080 to 9090)
```powershell
# Edit appsettings.Production.json with the new port
# Then restart
Restart-Service AnalytikaRCM
```

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Service won't start | Check Event Viewer → Windows Logs → Application |
| Port 8080 in use | `netstat -ano \| findstr :8080` then kill that PID |
| Database error | Ensure `J:\Bix\analytika-rcm\data\` exists and is writable |
| Health check fails | Wait 30s after restart: `curl http://localhost:8080/api/health` |

---

## Full Documentation

See **WINDOWS_JBIX_DEPLOYMENT.md** for complete configuration and maintenance guides.

---

**Default Credentials:** admin@ghafbi.ae / Admin@123  
**Service Name:** AnalytikaRCM  
**Port:** 8080  
**Database:** SQLite (J:\Bix\analytika-rcm\data\analytika.db)

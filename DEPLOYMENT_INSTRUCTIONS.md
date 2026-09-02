# Analytika RCM — Windows Server Deployment Instructions

**Status:** Ready for deployment  
**Target:** Windows Server at J:\Bix\analytika-rcm  
**Service Name:** AnalytikaRCM  
**Port:** 8080  
**Created:** 2026-09-02

---

## What Has Been Prepared

✅ **Deployment Script:** `deploy-analytika-jbix.ps1`
- Handles both GitHub-downloaded and pre-copied local builds
- Includes fallback service installation script (no external dependencies required)
- Automatically creates Windows Service "AnalytikaRCM"
- Configures environment variables for production deployment
- Verifies installation with health checks

✅ **Documentation:**
- `DEPLOYMENT_QUICK_START.md` — Fast reference guide (read this first)
- `WINDOWS_JBIX_DEPLOYMENT.md` — Complete reference with all options
- `DEPLOYMENT_INSTRUCTIONS.md` — This file

✅ **Build Artifacts:**
- Application published to `J:\Ghaf Bi\publish\`
- Ready to copy to `J:\Bix\analytika-rcm\`

---

## Complete Deployment Process

### On Your Development Machine (Windows)

#### 1. Build the Application

```powershell
# Open PowerShell as Administrator
cd "C:\path\to\analytika-rcm"

# Build in Release mode
dotnet publish -c Release -o "J:\Ghaf Bi\publish"

# Verify build succeeded
ls "J:\Ghaf Bi\publish\Analytika.exe"
```

Expected file: `J:\Ghaf Bi\publish\Analytika.exe` (~45 MB)

#### 2. Copy Build to Deployment Location

```powershell
# Create deployment directory
mkdir "J:\Bix\analytika-rcm"

# Copy all files (this is the actual app that will run)
Copy-Item "J:\Ghaf Bi\publish\*" "J:\Bix\analytika-rcm\" -Recurse -Force

# Verify the copy
ls "J:\Bix\analytika-rcm\Analytika.exe"
```

#### 3. Copy the Deployment Script

```powershell
# Copy the deployment script to the J:\Bix directory
Copy-Item ".\deploy-analytika-jbix.ps1" "J:\Bix\"

# Verify
ls "J:\Bix\deploy-analytika-jbix.ps1"
```

---

### On Windows Server (or target machine)

#### 4. Execute Deployment Script

**Open PowerShell as Administrator** and run:

```powershell
# Navigate to deployment directory
cd "J:\Bix"

# Run the deployment script
.\deploy-analytika-jbix.ps1 -TargetPath "J:\Bix\analytika-rcm" -InstallService $true
```

**Script will:**
1. Check if Analytika.exe exists (it should from step 2)
2. Skip GitHub download (files already present)
3. Create Windows Service "AnalytikaRCM"
4. Configure environment variables
5. Start the service
6. Verify health endpoint

**Expected Output:**
```
🚀 Analytika RCM — Windows Deployment
=====================================

📁 Creating directory: J:\Bix\analytika-rcm
✓ Analytika.exe already found locally. Skipping download.
⚙️  Installing as Windows Service...
[OK] Data directory: J:\Bix\analytika-rcm\data
[OK] Service registered
[OK] Service environment configured

=== Service is RUNNING ===
App URL  : http://0.0.0.0:8080
Data dir : J:\Bix\analytika-rcm\data

✓ Service: Running
✓ Health check: OK

🎉 Deployment Complete!
Access the app at: http://<server-ip>:8080
Default login: admin@ghafbi.ae / Admin@123
⚠️  CHANGE PASSWORD IMMEDIATELY!
```

#### 5. Verify Installation

```powershell
# Check service status
Get-Service AnalytikaRCM

# Test health endpoint
curl http://localhost:8080/api/health

# Should return:
# {"status":"ok","service":"Analytika RCM",...}
```

#### 6. Access the Application

Open browser and navigate to:
- **Local:** `http://localhost:8080`
- **From other machines on network:** `http://<server-ip>:8080`

**Default Credentials:**
- Email: `admin@ghafbi.ae`
- Password: `Admin@123`

⚠️ **CHANGE PASSWORD IMMEDIATELY** after first login

---

## Directory Structure After Deployment

```
J:\Bix\
├── analytika-rcm\                    ← Application directory
│   ├── Analytika.exe                 ← Main executable
│   ├── Analytika.dll                 ← Core library
│   ├── [other DLLs]                  ← Dependencies
│   ├── appsettings.json              ← Configuration
│   ├── appsettings.Production.json   ← Production config
│   ├── wwwroot/                      ← Static assets (HTML, CSS, JS)
│   ├── cs/, de/, es/, ...            ← Language files
│   ├── data/                         ← Database directory (created by service)
│   │   ├── analytika.db              ← SQLite database
│   │   └── ...
│   ├── logs/                         ← Log files (created by service)
│   └── install-service.ps1           ← Service installation script (downloaded)
│
├── deploy-analytika-jbix.ps1         ← Main deployment script
└── analytika-rcm.backup-[timestamp]/ ← Backup of previous installation (if any)
```

---

## Service Information

**Service Name:** AnalytikaRCM  
**Display Name:** Analytika RCM  
**Startup Type:** Automatic (starts on server boot)  
**Port:** 8080  
**URL:** `http://0.0.0.0:8080`

### Environment Variables Set by Deployment

```
DB_DIR = J:\Bix\analytika-rcm\data
ASPNETCORE_ENVIRONMENT = Production
ASPNETCORE_URLS = http://0.0.0.0:8080
```

---

## Database

### SQLite (Default)

- **Location:** `J:\Bix\analytika-rcm\data\analytika.db`
- **Auto-created:** Yes, on first service start
- **Backup:** Stop service → copy .db file → start service

### PostgreSQL (Optional)

To use PostgreSQL instead of SQLite:

1. Install PostgreSQL and create database
2. Edit `J:\Bix\analytika-rcm\appsettings.Production.json`
3. Update connection string
4. Restart service

See `WINDOWS_JBIX_DEPLOYMENT.md` for details.

---

## Common Tasks

### Check Service Status
```powershell
Get-Service AnalytikaRCM
```

### Restart Service
```powershell
Restart-Service AnalytikaRCM
```

### Stop Service
```powershell
Stop-Service AnalytikaRCM
```

### Start Service
```powershell
Start-Service AnalytikaRCM
```

### View Recent Logs
```powershell
Get-EventLog -LogName Application -Source "AnalytikaRCM" -Newest 20
```

### Backup Database
```powershell
Stop-Service AnalytikaRCM
Copy-Item "J:\Bix\analytika-rcm\data\analytika.db" "J:\Bix\analytika-rcm\backups\analytika-$(Get-Date -Format 'yyyy-MM-dd').db"
Start-Service AnalytikaRCM
```

---

## Firewall Configuration

Allow Windows Firewall to access port 8080:

```powershell
# Allow from local network
netsh advfirewall firewall add rule name="Analytika RCM" dir=in action=allow protocol=tcp localport=8080

# Or restrict to specific subnet (recommended)
netsh advfirewall firewall add rule name="Analytika RCM" dir=in action=allow protocol=tcp localport=8080 remoteip=192.168.1.0/24
```

---

## Troubleshooting

### Service Won't Start

1. **Check Event Viewer:**
   ```powershell
   eventvwr.msc
   # Look in: Windows Logs → Application → Look for errors from AnalytikaRCM
   ```

2. **Verify files are present:**
   ```powershell
   ls "J:\Bix\analytika-rcm\" | grep -E "\.exe|\.dll"
   ```

3. **Check data directory permissions:**
   ```powershell
   icacls "J:\Bix\analytika-rcm\data"
   ```

4. **Try running manually:**
   ```powershell
   cd "J:\Bix\analytika-rcm"
   .\Analytika.exe --urls "http://0.0.0.0:8080"
   ```

### Port 8080 Already in Use

```powershell
# Find what's using the port
netstat -ano | findstr :8080

# Kill the process (if safe to do so)
taskkill /PID <PID> /F

# Or change the port (see WINDOWS_JBIX_DEPLOYMENT.md)
```

### Health Check Fails

```powershell
# Wait longer for service to start
Start-Sleep -Seconds 30

# Then test
curl http://localhost:8080/api/health -Verbose
```

### High Memory Usage

Normal: 200-400 MB after startup  
If > 1 GB:

```powershell
# Check what's happening in logs
Get-EventLog -LogName Application -Source "AnalytikaRCM" -Newest 10

# Restart service
Restart-Service AnalytikaRCM
```

---

## Security Checklist

After successful deployment:

- [ ] ✅ Changed default admin password (admin@ghafbi.ae)
- [ ] ✅ Verified Windows Firewall is properly configured
- [ ] ✅ Scheduled daily database backups
- [ ] ✅ Configured HTTPS (via Cloudflare Tunnel or nginx)
- [ ] ✅ Created additional admin users
- [ ] ✅ Disabled default admin account (after creating real users)
- [ ] ✅ Enabled Windows Update
- [ ] ✅ Monitoring Event Viewer for errors

---

## External Access (Optional)

To access the application from the internet:

### Option 1: Cloudflare Tunnel (Recommended)

```powershell
# Install cloudflared
Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/download/2024.1.0/cloudflared-windows-amd64.exe" -OutFile "C:\Program Files\cloudflared\cloudflared.exe"

# Authenticate and set up tunnel
# See WINDOWS_JBIX_DEPLOYMENT.md for full instructions
```

### Option 2: nginx Reverse Proxy

Set up nginx on same or different machine as reverse proxy with SSL.

### Option 3: Direct Access (Not Recommended)

Use Cloudflare DDoS protection or similar security layer.

---

## Support

- **Repository:** https://github.com/jawahirps/analytika-rcm
- **Issues:** https://github.com/jawahirps/analytika-rcm/issues
- **Documentation:** WINDOWS_JBIX_DEPLOYMENT.md (comprehensive reference)

---

## Rollback (If Needed)

If deployment fails or you need to rollback:

```powershell
# Stop current service
Stop-Service AnalytikaRCM

# Restore from backup
Copy-Item "J:\Bix\analytika-rcm.backup-[timestamp]\*" "J:\Bix\analytika-rcm\" -Recurse -Force

# Start service
Start-Service AnalytikaRCM
```

---

## Version Information

| Item | Value |
|------|-------|
| Build Date | 2026-09-02 |
| Application Version | 1.0.0 |
| Runtime | Windows x64 (.NET 10) |
| Service Name | AnalytikaRCM |
| Port | 8080 |
| Default DB | SQLite |

---

**Ready for deployment!** Follow the steps above and the application will be running on your Windows Server.

For detailed configuration and maintenance, see **WINDOWS_JBIX_DEPLOYMENT.md**.  
For quick reference, see **DEPLOYMENT_QUICK_START.md**.

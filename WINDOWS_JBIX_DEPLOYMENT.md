# Analytika RCM — Windows Server Deployment Guide (J:\Bix)

**Last Updated:** 2026-09-02  
**Version:** 1.0.0  
**Platform:** Windows Server 2016+ / Windows 10+ Pro  
**Service Name:** AnalytikaRCM  
**Port:** 8080  
**Database:** SQLite (default) / PostgreSQL (configurable)

---

## Prerequisites

- **Windows Server 2016+** or **Windows 10/11 Pro**
- **Administrator privileges** required
- **.NET 10 runtime** — NOT required (self-contained build)
- **Disk space:** 500MB minimum (400MB app + 100MB data)
- **Build artifacts:** Analytika.exe and dependencies from `/publish` folder

---

## Deployment Steps

### Step 1: Prepare the Build

Build the application in Release mode:

```powershell
cd C:\path\to\analytika-rcm

# Build (Visual Studio or dotnet CLI)
dotnet publish -c Release -o "J:\Ghaf Bi\publish"

# Verify the build
ls "J:\Ghaf Bi\publish\Analytika.exe"
```

**Expected output:**
- `Analytika.exe` (~45 MB)
- `Analytika.dll` and dependencies
- `appsettings.json`, `appsettings.Production.json`
- `wwwroot/` (static assets)
- Language directories (`cs/`, `de/`, `es/`, etc.)

---

### Step 2: Copy Build to Deployment Location

Copy the published build to the target deployment directory:

```powershell
# Create target directory
mkdir "J:\Bix\analytika-rcm"

# Copy all files from publish to deployment location
Copy-Item "J:\Ghaf Bi\publish\*" "J:\Bix\analytika-rcm\" -Recurse -Force

# Verify Analytika.exe is present
ls "J:\Bix\analytika-rcm\Analytika.exe"
```

---

### Step 3: Run the Deployment Script

**Run as Administrator:**

```powershell
# Navigate to deployment directory
cd "J:\Bix"

# Download and run the deployment script
# Option A: If you have the script file locally
.\deploy-analytika-jbix.ps1 -TargetPath "J:\Bix\analytika-rcm" -InstallService $true

# Option B: Download from GitHub (if available)
$script = "https://raw.githubusercontent.com/jawahirps/analytika-rcm/main/deploy-analytika-jbix.ps1"
Invoke-WebRequest -Uri $script -OutFile ".\deploy-analytika-jbix.ps1"
.\deploy-analytika-jbix.ps1
```

**Script Actions:**
1. ✓ Creates target directory (backs up existing)
2. ✓ Verifies Analytika.exe is present
3. ✓ Creates/configures Windows Service "AnalytikaRCM"
4. ✓ Sets environment variables (DB_DIR, ASPNETCORE_ENVIRONMENT, ASPNETCORE_URLS)
5. ✓ Starts the service
6. ✓ Verifies health endpoint

**Expected Output:**
```
🚀 Analytika RCM — Windows Deployment
=====================================

📁 Creating directory: J:\Bix\analytika-rcm
✓ Analytika.exe found
✓ Service installed successfully
✓ Service: Running
✓ Health check: OK

🎉 Deployment Complete!
Access the app at: http://<server-ip>:8080
Default login: admin@ghafbi.ae / Admin@123
⚠️  CHANGE PASSWORD IMMEDIATELY!
```

---

## Step 4: Verify Installation

### Check Service Status

```powershell
# View service status
Get-Service AnalytikaRCM | Format-Table -AutoSize

# Expected output:
# Status   Name          DisplayName
# ------   ----          -----------
# Running  AnalytikaRCM  Analytika RCM
```

### Test Health Endpoint

```powershell
# Local test
curl http://localhost:8080/api/health

# Expected response:
# {"status":"ok","service":"Analytika RCM","timestamp":"2026-09-02T..."}
```

### View Logs

```powershell
# Windows Event Viewer
eventvwr.msc

# Or PowerShell
Get-EventLog -LogName Application -Source "AnalytikaRCM" -Newest 10
```

### Access the Application

1. **Local:** `http://localhost:8080`
2. **Network:** `http://<server-ip>:8080`
3. **Default Credentials:**
   - Email: `admin@ghafbi.ae`
   - Password: `Admin@123`
   - ⚠️ **CHANGE IMMEDIATELY AFTER FIRST LOGIN**

---

## Configuration

### Database

#### SQLite (Default)

Database auto-creates at: `J:\Bix\analytika-rcm\data\analytika.db`

**Verify:**
```powershell
dir "J:\Bix\analytika-rcm\data\"
```

#### PostgreSQL (Recommended for Production)

Edit `J:\Bix\analytika-rcm\appsettings.Production.json`:

```json
{
  "Database": {
    "Provider": "postgres"
  },
  "ConnectionStrings": {
    "Postgres": "Host=your-pg-server;Database=analytika;Username=user;Password=pass"
  }
}
```

Restart service:
```powershell
Restart-Service AnalytikaRCM
```

### Environment Variables

The deployment script automatically sets these at machine level:

```powershell
DB_DIR = J:\Bix\analytika-rcm\data
ASPNETCORE_ENVIRONMENT = Production
ASPNETCORE_URLS = http://0.0.0.0:8080
```

To modify:
```powershell
[System.Environment]::SetEnvironmentVariable("DB_DIR", "J:\Bix\analytika-rcm\data", "Machine")
# Then restart the service
Restart-Service AnalytikaRCM
```

### Port Configuration

To change the default port (8080):

1. Edit `appsettings.Production.json`:
```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:9090"
      }
    }
  }
}
```

2. Update firewall rule (see below)
3. Restart service:
```powershell
Restart-Service AnalytikaRCM
```

---

## Firewall Configuration

### Allow Port 8080

```powershell
# Allow all local network
netsh advfirewall firewall add rule name="Analytika RCM" dir=in action=allow protocol=tcp localport=8080

# Restrict to specific subnet (recommended)
netsh advfirewall firewall add rule name="Analytika RCM - Restricted" dir=in action=allow protocol=tcp localport=8080 remoteip=192.168.1.0/24
```

### Remove Firewall Rule

```powershell
netsh advfirewall firewall delete rule name="Analytika RCM"
```

---

## Service Management

### Start Service

```powershell
Start-Service AnalytikaRCM
```

### Stop Service

```powershell
Stop-Service AnalytikaRCM
```

### Restart Service

```powershell
Restart-Service AnalytikaRCM
```

### View Service Status

```powershell
Get-Service AnalytikaRCM
```

### Service Auto-Start

Service is configured to start automatically on boot. To disable:

```powershell
Set-Service -Name AnalytikaRCM -StartupType Manual
```

To re-enable:

```powershell
Set-Service -Name AnalytikaRCM -StartupType Automatic
```

---

## Maintenance

### Backup Database

```powershell
# Stop service
Stop-Service AnalytikaRCM

# Create backup directory
mkdir "J:\Bix\analytika-rcm\backups"

# Backup database
Copy-Item "J:\Bix\analytika-rcm\data\analytika.db" "J:\Bix\analytika-rcm\backups\analytika-$(Get-Date -Format 'yyyy-MM-dd-HHmmss').db"

# Backup encryption keys (if using data protection)
Copy-Item "J:\Bix\analytika-rcm\dataprotection-keys" "J:\Bix\analytika-rcm\backups\dataprotection-keys-$(Get-Date -Format 'yyyy-MM-dd-HHmmss')" -Recurse

# Start service
Start-Service AnalytikaRCM
```

### Automated Daily Backup (Task Scheduler)

Create a scheduled task to run backups daily:

```powershell
$taskName = "Analytika RCM Daily Backup"
$action = New-ScheduledTaskAction -Execute "PowerShell.exe" -Argument "-File C:\scripts\backup-analytika.ps1"
$trigger = New-ScheduledTaskTrigger -Daily -At 02:00AM
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -RunLevel Highest
```

### Update Application

When a new version is released:

```powershell
# 1. Stop service
Stop-Service AnalytikaRCM

# 2. Backup current installation
Copy-Item "J:\Bix\analytika-rcm" "J:\Bix\analytika-rcm.backup-$(Get-Date -Format 'yyyy-MM-dd')" -Recurse

# 3. Replace exe/dll files with new build
# Copy files from new published build to J:\Bix\analytika-rcm\

# 4. Start service
Start-Service AnalytikaRCM

# 5. Verify
curl http://localhost:8080/api/health
```

### View Real-Time Logs

```powershell
# View last 20 lines and follow
Get-Content "J:\Bix\analytika-rcm\logs\analytika*.log" -Wait -Tail 20
```

---

## Troubleshooting

### Service Won't Start

**Check 1: View event logs**
```powershell
eventvwr.msc
# Navigate to: Windows Logs > Application > Look for "AnalytikaRCM" errors
```

**Check 2: Verify all files are present**
```powershell
ls "J:\Bix\analytika-rcm\" | grep -E "\.exe|\.dll"
```

**Check 3: Verify permissions**
```powershell
icacls "J:\Bix\analytika-rcm\data" /grant Everyone:F
```

**Check 4: Try manual start**
```powershell
cd "J:\Bix\analytika-rcm"
.\Analytika.exe --urls "http://0.0.0.0:8080"
```

### Port 8080 Already in Use

```powershell
# Find process using port 8080
netstat -ano | findstr :8080

# Kill process (careful!)
taskkill /PID <PID> /F

# Or change port (see Configuration section above)
```

### Database Connection Errors

**SQLite:**
```powershell
# Check if data directory exists and is writable
dir "J:\Bix\analytika-rcm\data\"
icacls "J:\Bix\analytika-rcm\data"
```

**PostgreSQL:**
```powershell
# Test connection
psql -h your-pg-server -U user -d analytika

# Verify connection string in appsettings.Production.json
```

### High Memory Usage

Normal: 200-400 MB after startup  
If > 1 GB: Check logs for errors, restart service

```powershell
Get-Process Analytika* | Select-Object Name, WorkingSet
```

### Health Check Fails

```powershell
# Wait 30 seconds for app to start
Start-Sleep -Seconds 30
curl http://localhost:8080/api/health -Verbose

# Check service logs
Get-EventLog -LogName Application -Source "AnalytikaRCM" -Newest 5
```

---

## Security Recommendations

1. **Change default password immediately** after first login
2. **Enable HTTPS** using:
   - Cloudflare Tunnel (easiest, no cert management)
   - nginx reverse proxy with Let's Encrypt
   - Local SSL certificate
3. **Restrict firewall** to specific IP ranges (see Firewall Configuration)
4. **Enable daily backups** (see Maintenance section)
5. **Keep Windows Server patched** - enable Windows Update
6. **Monitor event logs** regularly for errors/warnings
7. **Use strong admin passwords**
8. **Disable default admin account** after creating real users

---

## External Access (Cloudflare Tunnel)

To access the application from the internet securely:

### Install Cloudflare Tunnel

```powershell
# 1. Download cloudflared
Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/download/2024.1.0/cloudflared-windows-amd64.exe" -OutFile "C:\Program Files\cloudflared\cloudflared.exe"

# 2. Authenticate
cd "C:\Program Files\cloudflared"
.\cloudflared.exe tunnel login

# 3. Create tunnel
.\cloudflared.exe tunnel create analytika

# 4. Install as service
.\cloudflared.exe service install

# 5. Start service
net start cloudflared
```

### Configure DNS

Follow the prompts to set up a Cloudflare domain (e.g., `analytika.your-domain.com`)

---

## Support & Documentation

- **Repository:** https://github.com/jawahirps/analytika-rcm
- **Issues:** https://github.com/jawahirps/analytika-rcm/issues
- **Email:** support@ghafbi.ae

---

## Build Information

| Property | Value |
|----------|-------|
| Build Date | 2026-09-02 |
| Version | 1.0.0 |
| Runtime | Windows x64 (.NET 10) |
| Package Size | ~164 MB |
| Database | SQLite (default) / PostgreSQL |
| Service Port | 8080 |

---

**Last Deployed:** [Update with deployment date]  
**Deployed By:** [Update with administrator name]  
**Deployment Status:** ✅ Active

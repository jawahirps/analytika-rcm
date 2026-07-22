# Windows Server Deployment — Analytika RCM

Complete guide to deploy Analytika RCM as a Windows Service on an office server
with public HTTPS access via Cloudflare Tunnel.

---

## Prerequisites

| Requirement | Minimum |
|---|---|
| OS | Windows Server 2019+ or Windows 10/11 Pro |
| .NET | Not required (self-contained publish) |
| RAM | 2 GB free |
| Disk | 10 GB + space for your database |
| Network | Outbound HTTPS (port 443) for Cloudflare Tunnel |

---

## Step 1 — Build the Package (on your dev machine)

```powershell
# Clone the repo (if you haven't already)
git clone https://github.com/ghafbi/analytika-rcm.git
cd analytika-rcm

# Requires .NET 10 SDK on your dev machine
# Download: https://dotnet.microsoft.com/download/dotnet/10.0

# Build the self-contained Windows package
.\deploy\1_publish.ps1
```

This creates `deploy\output\` containing everything the server needs — no
.NET installation required on the target machine.

**Copy the entire `output` folder to the server** (USB drive, network share, or
`scp`).

---

## Step 2 — Install the Service (on the server)

Open **PowerShell as Administrator** in the folder you copied:

```powershell
.\2_install_service.ps1
```

What it does:
- Creates `C:\GhafBI\data` for the SQLite database
- Registers `GhafBI` as a Windows Service (auto-start on boot)
- Configures auto-restart on failure (5s → 10s → 30s backoff)
- Starts the app on `http://localhost:5200`

Verify by opening `http://localhost:5200` in a browser on the server.

### First Login

| Field | Value |
|---|---|
| Email | `admin@ghafbi.ae` |
| Password | `Admin@123` |

**Change this password immediately.**

---

## Step 3 — Public HTTPS Access (Cloudflare Tunnel)

Expose the app securely to the internet with zero open firewall ports.

### 3a. Install Cloudflare

```powershell
winget install Cloudflare.cloudflared
```

### 3b. Authenticate

```powershell
cloudflared tunnel login
# Opens browser — log in to your Cloudflare account
```

### 3c. Create the Tunnel

```powershell
cloudflared tunnel create ghafbi
# Note the Tunnel ID printed (e.g. a1b2c3d4-xxxx-xxxx-xxxx-xxxxxxxxxxxx)
```

### 3d. Configure

Edit `3_cloudflared_config.yml`:
- Replace `YOUR_TUNNEL_ID` with your tunnel ID
- Replace `YOUR_USERNAME` with your Windows username

Save it to: `C:\Users\<you>\.cloudflared\config.yml`

### 3e. Create DNS Record

```powershell
cloudflared tunnel route dns ghafbi bix.ghafservices.com
```

### 3f. Install as Service

```powershell
cloudflared service install
net start cloudflared
```

Prefer the dedicated tunnel installer when an existing token-based
`cloudflared` service must stay intact:

```powershell
# After copying config to C:\GhafBI\cloudflared\config.yml
.\3b_install_tunnel_service.ps1
```

### 3g. Verify

Open `https://bix.ghafservices.com` from any device — you should see the login page.

---

## Updating

### Option A — Manual Update

1. Build a new package on your dev machine: `.\deploy\1_publish.ps1`
2. Copy the `output` folder to the server (overwrite the existing files)
3. Run `.\5_update.ps1` as Administrator

The update script stops the service, swaps the files, restarts, and auto-rolls
back if the new version fails to start.

### Option B — Automated via GitHub (recommended)

Set up a scheduled task that pulls the latest release:

```powershell
# Create a scheduled task that runs nightly at 3 AM
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument @"
-NoProfile -ExecutionPolicy Bypass -File C:\GhafBI\auto-update.ps1
"@
$trigger = New-ScheduledTaskTrigger -Daily -At "3:00AM"
Register-ScheduledTask -TaskName "GhafBI-AutoUpdate" -Action $action -Trigger $trigger -RunLevel Highest
```

Create `C:\GhafBI\auto-update.ps1`:

```powershell
$ErrorActionPreference = "Stop"
$AppDir = "C:\GhafBI\app"
$RepoUrl = "https://github.com/ghafbi/analytika-rcm"

# Check for new release
$latest = (Invoke-RestMethod "$RepoUrl/releases/latest").tag_name
$currentFile = "$AppDir\version.txt"
$current = if (Test-Path $currentFile) { Get-Content $currentFile } else { "" }

if ($latest -eq $current) { exit 0 }

# Download and extract
$zip = "$env:TEMP\analytika-$latest.zip"
Invoke-WebRequest "$RepoUrl/releases/download/$latest/analytika-win-x64.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath $AppDir -Force

# Update and restart
& "$AppDir\5_update.ps1"
$latest | Set-Content $currentFile
```

---

## Uninstalling

```powershell
# Run as Administrator
.\4_uninstall.ps1
```

This removes the Windows Service and clears environment variables.
Your data in `C:\GhafBI\data` is preserved — delete manually if needed.

---

## Firewall Rules

The app only listens on `localhost:5200` — **no inbound firewall rules needed**.
Cloudflare Tunnel creates an outbound-only connection to Cloudflare's edge.

If you need LAN access without Cloudflare:

```powershell
# Allow LAN access on port 5200
New-NetFirewallRule -DisplayName "GhafBI RCM" -Direction Inbound -Port 5200 -Protocol TCP -Action Allow

# Then change the service URL to bind all interfaces:
# Edit 2_install_service.ps1: $AppUrl = "http://+:5200"
# Re-run 2_install_service.ps1
```

---

## Backups

### Automated Daily Backup

Create `C:\GhafBI\backup.ps1`:

```powershell
$DataDir = "C:\GhafBI\data"
$BackupDir = "C:\GhafBI\backups"
$Date = Get-Date -Format "yyyy-MM-dd"

New-Item -ItemType Directory -Force $BackupDir | Out-Null

# Copy database (SQLite safe copy while service runs — uses snapshot isolation)
Copy-Item "$DataDir\analytika.db" "$BackupDir\analytika-$Date.db"

# Copy data protection keys (needed to decrypt portal credentials)
Copy-Item "$DataDir\dataprotection-keys" "$BackupDir\dp-keys-$Date" -Recurse -Force

# Clean backups older than 30 days
Get-ChildItem $BackupDir -Recurse | Where-Object {
    $_.LastWriteTime -lt (Get-Date).AddDays(-30)
} | Remove-Item -Recurse -Force

Write-Host "Backup complete: $BackupDir\analytika-$Date.db"
```

Schedule it:

```powershell
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
  -Argument "-NoProfile -ExecutionPolicy Bypass -File C:\GhafBI\backup.ps1"
$trigger = New-ScheduledTaskTrigger -Daily -At "2:00AM"
Register-ScheduledTask -TaskName "GhafBI-Backup" -Action $action -Trigger $trigger -RunLevel Highest
```

---

## Troubleshooting

| Issue | Fix |
|---|---|
| Service won't start | Check Event Viewer → Windows Logs → Application |
| Port 5200 in use | `netstat -ano \| findstr 5200` → kill the process or change port |
| Cloudflare tunnel disconnects | Run `cloudflared tunnel info ghafbi` to check status |
| Database locked errors | Only one instance should run — check `Get-Service GhafBI` |
| Forgot admin password | Delete `C:\GhafBI\data\analytika.db`, re-run with seed enabled |

### Service Management

```powershell
# Check status
Get-Service GhafBI

# Start / Stop / Restart
Start-Service GhafBI
Stop-Service GhafBI
Restart-Service GhafBI

# View logs (last 50 events)
Get-EventLog -LogName Application -Source GhafBI -Newest 50
```

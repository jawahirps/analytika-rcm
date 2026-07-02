# How to Deploy Analytika RCM

Pick the deployment method that fits your infrastructure. Every option uses the
same Docker image (`ghcr.io/ghafbi/analytika-rcm:latest`) built automatically
on each push to `main`.

---

## Quick Reference

| Method | Monthly Cost | Best For | DB |
|---|---|---|---|
| **Azure App Service** | ~$25 | Managed cloud, auto-scale, zero ops | PostgreSQL |
| **Oracle Cloud (Free)** | $0 | Budget hosting, large DB | SQLite |
| **Railway / Render** | $5–20 | Quick PaaS deploy | PostgreSQL |
| **Windows Server** | On-prem cost | Office/on-prem installs | SQLite |
| **Any Linux VPS** | $5–12 | Full control, cheap | SQLite or PostgreSQL |

---

## Option 1 — Azure App Service (Recommended)

### One-Click Script

```bash
# Prerequisites: Azure CLI installed + logged in
az login

# Run the setup script (creates everything in ~5 min)
./deploy/azure-setup.sh  [resource-group]  [app-name]  [location]

# Example:
./deploy/azure-setup.sh  analytika-rg  analytika  eastus
```

This provisions:
- **Azure Database for PostgreSQL** Flexible Server (B1ms — ~$12/mo)
- **App Service Plan** (Linux B1 — ~$13/mo)
- **Web App for Containers** pulling from GHCR
- All environment variables pre-configured
- Health check on `/health`

### Enable Auto-Deploy from GitHub

After the script runs, connect GitHub Actions:

```bash
# 1. Create a service principal
az ad sp create-for-rbac --name "analytika-deploy" \
  --role contributor \
  --scopes /subscriptions/$(az account show --query id -o tsv)/resourceGroups/analytika-rg \
  --json-auth
```

2. Go to **GitHub repo → Settings → Secrets and variables → Actions**
3. Add secret: `AZURE_CREDENTIALS` → paste the JSON output from step 1
4. Add variable: `AZURE_DEPLOY_ENABLED` → `true`
5. Add variable: `AZURE_WEBAPP_NAME` → your app name (e.g. `analytika`)

Now every push to `main` auto-deploys to Azure.

### First Login

- URL: `https://<app-name>.azurewebsites.net`
- Credentials: `admin@ghafbi.ae` / `Admin@123`
- Change the password immediately after first login.

---

## Option 2 — Oracle Cloud Always Free ($0/month)

Best for large SQLite databases (40–50 GB+) on a beefy free VM.

```bash
# 1. Create Oracle Cloud VM:
#    Image: Ubuntu 24.04 | Shape: VM.Standard.A1.Flex (4 OCPU / 24 GB RAM)
#    Boot volume: 150–200 GB | Keep only port 22 open

# 2. SSH in and install Docker
ssh ubuntu@<vm-ip>
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER && newgrp docker

# 3. Clone and configure
git clone https://github.com/ghafbi/analytika-rcm.git
cd analytika-rcm/deploy/linux
cp .env.example .env
nano .env   # paste your Cloudflare Tunnel token

# 4. Launch
docker compose up -d
docker compose logs -f app   # watch for "Now listening on http://[::]:8080"
```

Public HTTPS access is via **Cloudflare Tunnel** (no open ports needed).
See `deploy/linux/README.md` for the full runbook including backups and
auto-updates via Watchtower.

---

## Option 3 — Railway / Render

### Railway

1. Go to [railway.app](https://railway.app) → **New Project → Deploy from GitHub**
2. Select the `analytika-rcm` repo
3. Railway auto-detects the Dockerfile
4. Add a **PostgreSQL** plugin (one click)
5. Set environment variables:

```
Database__Provider=postgres
StartupMaintenance__RunDatabaseSetupOnStartup=true
StartupMaintenance__SeedDataOnStartup=true
BackgroundJobs__HangfireServerEnabled=true
BackgroundJobs__RecurringJobsEnabled=true
```

Railway auto-injects `DATABASE_URL` from the PostgreSQL plugin.

### Render

1. Go to [render.com](https://render.com) → **New → Web Service**
2. Connect your GitHub repo
3. Set **Docker** as the runtime
4. Add a **PostgreSQL** database
5. Set the same environment variables as Railway above
6. Add secret `RENDER_DEPLOY_HOOK_URL` to your GitHub repo for auto-deploy

Both platforms auto-deploy on every push to `main`.

---

## Option 4 — Windows Server (On-Premises)

For office/on-prem installations running as a Windows Service.

```powershell
# 1. On your dev machine: build a self-contained Windows package
.\deploy\1_publish.ps1

# 2. Copy the output folder to the server via USB/network share

# 3. On the server (Run as Administrator):
.\2_install_service.ps1

# 4. (Optional) Set up Cloudflare Tunnel for external access
#    See deploy\3_cloudflared_config.yml
```

The app runs as a Windows Service, auto-starts on boot, and uses SQLite.

---

## Option 5 — Any Linux VPS (Docker Compose)

Works on DigitalOcean, Linode, Hetzner, AWS EC2, or any $5/mo VPS.

```bash
# 1. Install Docker
curl -fsSL https://get.docker.com | sh

# 2. Clone and start
git clone https://github.com/ghafbi/analytika-rcm.git
cd analytika-rcm

# SQLite mode (simplest):
docker compose up -d

# PostgreSQL mode (recommended for production):
# Set DATABASE_URL in docker-compose.yml or .env, then:
docker compose up -d
```

Put nginx or Caddy in front for SSL:

```bash
# Caddy (auto-SSL, zero config)
sudo apt install caddy
echo "yourdomain.com { reverse_proxy localhost:80 }" | sudo tee /etc/caddy/Caddyfile
sudo systemctl restart caddy
```

---

## Environment Variables Reference

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Runtime environment |
| `Database__Provider` | `sqlite` | `sqlite` or `postgres` |
| `ConnectionStrings__Postgres` | — | Npgsql connection string |
| `DATABASE_URL` | — | Railway/Render-style postgres URL (auto-detected) |
| `DB_DIR` | `/app/data` | SQLite database directory |
| `StartupMaintenance__RunDatabaseSetupOnStartup` | `false` | Create schema on first boot |
| `StartupMaintenance__SeedDataOnStartup` | `false` | Seed admin user on first boot |
| `BackgroundJobs__HangfireServerEnabled` | `false` | Enable background job processing |
| `BackgroundJobs__RecurringJobsEnabled` | `false` | Enable nightly portal sync |
| `Logging__JsonConsole` | `false` | Structured JSON logs for cloud platforms |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | OpenTelemetry collector URL |
| `WEBSITES_PORT` | — | Azure App Service: set to `8080` |

---

## CI/CD Pipeline

The release workflow (`.github/workflows/release.yml`) runs on every push to `main`:

1. Builds the Docker image
2. Pushes to GitHub Container Registry (`ghcr.io/ghafbi/analytika-rcm`)
3. Triggers deploy hooks for whichever platforms are enabled:
   - Azure: `AZURE_DEPLOY_ENABLED=true` + `AZURE_CREDENTIALS` secret
   - Render: `RENDER_DEPLOY_ENABLED=true` + `RENDER_DEPLOY_HOOK_URL` secret
   - Railway: `RAILWAY_DEPLOY_ENABLED=true` + `RAILWAY_REDEPLOY_HOOK_URL` secret

---

## Health Checks

- **`/health`** — basic liveness probe (200 OK)
- **`/healthz`** — deep check: DB connectivity + portal sync staleness

Configure your platform's health check to hit `/health` with a 60s interval.

---

## Backups

**PostgreSQL** (Azure/Railway/Render): Use the platform's managed backup.

**SQLite** (on-prem/VPS):
```bash
# Integrity-safe backup (runs inside the container)
docker compose exec app sqlite3 /app/data/analytika.db ".backup /app/data/backup.db"
```

Back up the `dataprotection-keys` directory alongside the database — without
these keys, encrypted portal credentials cannot be decrypted.

---

## Default Admin Credentials

| Email | Password |
|---|---|
| `admin@ghafbi.ae` | `Admin@123` |

**Change this immediately after first login.**

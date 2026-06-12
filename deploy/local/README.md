# Running Analytika RCM locally (your own machine)

Two ways — pick whichever your machine has. Both serve the app at
**http://localhost:8080** with a login page.

> The repo's *code* is the source of truth (GitHub `main`). There is nothing to
> copy from any cloud sandbox — a sandbox only ever has a throwaway empty DB.
> Your real data lives on the production host (`bix`) at
> `deploy/linux/data/analytika.db`. See **Data** below.

## Option A — Docker (no .NET install, no build)
Pulls the prebuilt image from GHCR.

```bash
git clone https://github.com/jawahirps/analytika-rcm.git   # or: git pull
cd analytika-rcm/deploy/local
docker compose up -d
docker compose logs -f app          # wait for: Now listening on http://[::]:8080
```
Open http://localhost:8080.

If the pull fails with a 403/auth error, the GHCR package is private — make it
public once (GitHub → repo → **Packages** → `analytika-rcm` → **Package
settings** → **Change visibility → Public**), or `docker login ghcr.io` with a
`read:packages` token.

## Option B — .NET SDK (run from source, no Docker)
Needs the .NET 10 SDK (https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/jawahirps/analytika-rcm.git
cd analytika-rcm/Analytika
dotnet run -c Release
```
The console prints the URL (e.g. http://localhost:5000). The SQLite DB
(`analytika.db`) is created next to the app on first run.

## Data
The DB starts **empty** (login still works with the seeded admin). To use real data:

1. **Copy production data in** — from the bix host, copy
   `deploy/linux/data/analytika.db` to:
   - Docker: `deploy/local/data/analytika.db` (create the folder) **before** `docker compose up`
   - dotnet run: `Analytika/analytika.db`
   Leave all `StartupMaintenance__*` flags = `false`.
   > It's PHI and ~50 GB — copy over a trusted channel (scp/rclone), not the
   > in-app uploader.

2. **Or start fresh** — first boot only, set
   `StartupMaintenance__RunDatabaseSetupOnStartup=true` (Docker compose env, or
   appsettings), start once to create the schema, then set it back to `false`.

## First login
Default admin is seeded on a fresh DB — change the password immediately under
**Admin → Users**. Add portal credentials under **Admin → Credentials**
(encrypted at rest).

## Stop / update
```bash
docker compose down                 # stop; ./data is preserved
docker compose pull && docker compose up -d   # update to the latest image
```

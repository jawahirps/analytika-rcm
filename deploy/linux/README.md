# Running Analytika RCM on Oracle Cloud (Always Free)

This stands the app up on an Oracle Cloud **Always Free** Ampere (ARM) VM with a
public HTTPS URL via Cloudflare Tunnel — **$0/month**, fits the ~40–50 GB SQLite DB.

> Background jobs (nightly DHA/RHA sync + pending downloads) are **ON** in
> `docker-compose.yml` here — unlike the Render/Railway configs, which disable them.

## 1. Create the VM (Oracle Cloud Console)
- **Compute → Instances → Create**
- Image: **Ubuntu 24.04**, Shape: **VM.Standard.A1.Flex** (Ampere/ARM) →
  **4 OCPU / 24 GB RAM** (within Always Free)
- Boot volume: **bump to ~150–200 GB** (Always Free allows up to 200 GB block storage) to hold the DB + downloads
- Add your SSH public key
- Networking: leave default. **Do not** open port 8080 — Cloudflare Tunnel handles ingress. (Only keep 22/SSH.)

> If A1 capacity is "out of capacity" at create time, retry in another AD/region, or
> upgrade the account to Pay-As-You-Go (still $0 within Always-Free limits).

## 2. Install Docker on the VM
```bash
ssh ubuntu@<vm-public-ip>
sudo apt-get update && sudo apt-get install -y git curl
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER && newgrp docker
```

## 3. Get the code + configure
```bash
git clone https://github.com/jawahirps/analytika-rcm.git
cd analytika-rcm/deploy/linux
cp .env.example .env
nano .env          # paste your Cloudflare tunnel token (see step 5)
```

## 4. Bring up the database
- **Migrating existing data:** copy your `analytika.db` onto the VM into
  `deploy/linux/data/` (use `scp` or `rclone` — for 50 GB don't use the in-app
  upload). Leave all `StartupMaintenance__*` = `false`.
- **Fresh start:** set `StartupMaintenance__RunDatabaseSetupOnStartup: "true"` in
  `docker-compose.yml`, start once (creates schema), then set it back to `"false"`.

## 5. Public URL via Cloudflare Tunnel (no open ports)
- Cloudflare **Zero Trust → Networks → Tunnels → Create tunnel** → copy the token into `.env`.
- Add a **Public Hostname**: e.g. `bix.ghafservices.com` → Service `http://app:8080`.
  (`app` is the compose service name; cloudflared resolves it on the compose network.)

## 6. Launch
```bash
docker compose up -d --build      # first build ~3–5 min on A1
docker compose logs -f app        # watch startup; expect "Now listening on http://[::]:8080"
```
Open your Cloudflare hostname in a browser → login page should load.

## 7. Go live
- Admin → **Portal Credentials**: add each facility (eClaim + Riyati) — encrypted store.
- Portal → **Sync All Facilities** for the back-fill; cron sync then runs nightly (02:00).
- Portal → **Data Validation** to verify counts.

## 8. Backups (do this — it's PHI)
```bash
# nightly: integrity-safe SQLite backup + offsite copy (e.g. Backblaze B2 via rclone)
0 3 * * * cd ~/analytika-rcm/deploy/linux && \
  docker compose exec -T app sh -c 'apt-get -qq install -y sqlite3 2>/dev/null; sqlite3 /app/data/analytika.db ".backup /app/data/backup.db"' && \
  gzip -c data/backup.db > data/backup-$(date +\%F).db.gz
```
(50 GB on Backblaze B2 ≈ $0.30/mo; or Oracle Object Storage 20 GB free for partials.)

## Updating later
```bash
cd ~/analytika-rcm && git pull origin main
cd deploy/linux && docker compose up -d --build
```

## Notes
- The image is now architecture-portable (no pinned RID), so it builds natively on A1/ARM.
- Resource use is light (~150–300 MB RAM idle); A1's 24 GB is ample headroom for sync + reports.

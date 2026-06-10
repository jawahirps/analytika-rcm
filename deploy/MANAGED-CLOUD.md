# Managed Cloud Deployment (v2.0)

v2.0 makes the platform safely hostable as single-tenant cloud pods while
remaining 100% backward-compatible with existing SQLite/on-prem installs.

## What changed in v2.0

| Area | SQLite / on-prem (unchanged) | Managed cloud (new) |
|---|---|---|
| Database | SQLite file, `EnsureCreated` + raw-SQL maintenance | **PostgreSQL** via EF Core migrations (`Migrate()` on startup) |
| Hangfire jobs | In-memory (lost on restart) | **Postgres storage** — queued & recurring jobs survive restarts/deploys |
| Portal credentials | — | **Encrypted at rest** (ASP.NET Data Protection, `dpv1:` prefix); legacy Base64 values auto-upgrade on first startup |
| Health probe | — | **`/healthz`** (checks DB connectivity); wired into `railway.toml` / `render.yaml` |
| DB backup page | File download/upload (SQLite only) | Gated off — use the platform's `pg_dump`/`pg_restore` |

Data Protection keys are persisted to `<data dir>/dp-keys` — on Docker that is
the `/app/data` volume, so cookies and credential decryption survive redeploys.
**Back up `dp-keys` with the database**: without the keys, encrypted credentials
cannot be decrypted.

## Configuration

Provider selection (either works):

```bash
# explicit
Database__Provider=postgres
ConnectionStrings__Postgres="Host=...;Database=analytika;Username=...;Password=..."

# or zero-config on Railway/Render/Heroku-style platforms:
DATABASE_URL=postgres://user:pass@host:5432/analytika   # auto-selects postgres
```

If neither is set, the app runs exactly as before on SQLite (`DB_DIR`).

Recommended pod settings:

```bash
ASPNETCORE_ENVIRONMENT=Production
DATABASE_URL=...                                      # from the managed Postgres add-on
DB_DIR=/app/data                                      # volume: dp-keys + report/download caches
StartupMaintenance__SeedDataOnStartup=true            # first boot only, then false
BackgroundJobs__HangfireServerEnabled=true
BackgroundJobs__RecurringJobsEnabled=true
BackgroundJobs__PendingDownloads__HostedServiceEnabled=true
```

Schema is applied automatically: on Postgres, startup runs EF migrations
(`Analytika/Migrations/`). To add a migration after changing the model:

```bash
dotnet tool install -g dotnet-ef
cd Analytika && dotnet ef migrations add <Name> --output-dir Migrations
```

## Monitoring (v2.0 scale chain)

- **`/healthz`** aggregates two checks: DB connectivity and **portal-sync
  staleness** — Degraded when active credentials exist but no portal fetch has
  succeeded within `Monitoring__SyncStaleAfterHours` (default 26). Alert on
  Degraded; it intentionally does not fail the probe, so platforms don't
  restart pods over portal-side outages.
- **OpenTelemetry** (traces + metrics, ASP.NET/HTTP-client/runtime
  instrumentation) exports via OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set
  (Grafana Cloud, Better Stack, any collector). `OTEL_SERVICE_NAME` defaults
  to `ghaf-bix`. Unset = zero overhead.
- **Structured JSON logs**: set `Logging__JsonConsole=true` so platform log
  aggregators can index fields instead of scraping text.

## Automatic updates (CI/CD)

`.github/workflows/release.yml`: every push to `main` (and every `v*` tag)
builds the Docker image and publishes it to GHCR
(`ghcr.io/<owner>/analytika-rcm:latest|vX.Y.Z|sha-…`). To enable automatic
rollout, set repo variables `RENDER_DEPLOY_ENABLED` / `RAILWAY_DEPLOY_ENABLED`
to `true` and add the matching `RENDER_DEPLOY_HOOK_URL` /
`RAILWAY_REDEPLOY_HOOK_URL` secrets. Schema changes ride along automatically
via migrations-on-startup.

## Still to come (scale chain)

- **Stripe billing** — per-facility subscription quantities reported nightly;
  needs a Stripe account, price IDs and a customer-registry decision.
- **Onboarding wizard** — self-service facility + credential setup UI on top
  of the existing `TestCredential` live-validation endpoint and first-sync
  backfill.

## File storage note

Canonical claim/remittance XML is stored **in the database**
(`PortalTransactions.FileContentXml`); the `wwwroot/portal-downloads` copies are
non-critical caches and generated reports live on the mounted volume. Object
storage (S3/R2) is therefore not required for single-tenant pods — it becomes
relevant with the multi-tenant phase.

## Verified

Smoke-tested against PostgreSQL 16: migrations create all 25 tables, Hangfire
provisions its `hangfire` schema with both recurring jobs registered,
seeding completes, `/healthz` and login return 200, and a legacy Base64
credential was auto-upgraded to encrypted storage on restart. The SQLite path
was re-tested unchanged.

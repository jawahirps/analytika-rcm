# AGENTS.md

## Cursor Cloud specific instructions

Analytika is a .NET 10 ASP.NET Core MVC app (healthcare Revenue Cycle Management analytics for the UAE market). The repo also contains an optional Expo/React Native prototype in `mobile/` that runs on local sample data and is **not** wired to the backend, so it is not required to run/test the core product.

The update script already runs `dotnet restore Analytika.sln` on startup. The .NET 10 SDK is baked into the VM image (`/usr/share/dotnet`, symlinked to `/usr/local/bin/dotnet`); if `dotnet` is somehow missing, reinstall with the official script (`curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir /usr/share/dotnet`).

### Build / test / lint (backend — the core product)
- Build: `dotnet build Analytika.sln -c Debug` (see `.github/workflows/dotnet.yml`).
- Test: `dotnet test Analytika.sln` — xUnit suite in `Analytika.Tests` (45 tests, SQLite-backed).
- Lint: there is **no** separate linter or `.editorconfig`; CI treats the analyzer-backed build as the check. `dotnet format` reports pre-existing whitespace/style diffs that are **not** enforced by CI — do not "fix" them unless asked.
- The restore/build emit pre-existing warnings (duplicate `PackageReference`, NuGet vulnerability advisories, nullable/EF analyzer warnings). These are expected; the build reports `0 Error(s)`.

### Running the app (dev)
- Run: `dotnet run --project Analytika/Analytika.csproj --no-launch-profile`.
- There is no `launchSettings.json`; set the URL explicitly with `ASPNETCORE_URLS` (e.g. `http://0.0.0.0:5000`). The mac helper (`start-mac.sh`) uses port 5000; the Windows helper uses 5200; Docker/production uses 8080. A `PORT` env var (PaaS) overrides the URL.
- Default DB is **SQLite** (no external server needed). Set `DB_DIR` to control where `analytika.db` and the `dp-keys`/`dataprotection-keys` folder are written (e.g. `DB_DIR=/workspace/.devdata`); otherwise it lands next to the content root. PostgreSQL is only used when `Database:Provider=postgres` or `DATABASE_URL`/`ConnectionStrings:Postgres` is set.
- In `Development`, `StartupMaintenance` auto-creates the SQLite schema and seeds a default admin on first run. Login with **`admin@ghafbi.ae` / `Admin@123`**.
- Gotcha: on the very first run against a fresh DB, startup logs a `SQLite Error 1: 'no such table: PortalCredentials'` from the credential-encryption-upgrade step (it runs before schema creation) plus SMTP/OTEL "not configured" warnings. These are **non-fatal** — the app finishes booting, `/healthz` returns `Healthy`, and the UI works. `/healthz` is the deep health check; `/health` is not mapped in dev.
- External portal sync (DHA/DHPO, RHA), SMTP, Hangfire background jobs, and OpenTelemetry are all optional and off/unconfigured by default; the app runs and serves dashboards without them.

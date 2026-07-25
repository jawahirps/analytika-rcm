# CLAUDE.md

Guidance for Claude Code (and other AI assistants) working in this repository.

`AGENTS.md` holds the short environment/bootstrap notes for cloud dev VMs. This file is the
deeper map: architecture, data flow, conventions, and the traps that are easy to fall into.

---

## 1. What this is

**Analytika RCM** (product name **Ghaf Bi** / **Bix**) is a healthcare **Revenue Cycle
Management** analytics platform for the UAE market, built as a **.NET 10 ASP.NET Core MVC**
app (server-rendered Razor, no SPA).

It pulls claim and remittance files from UAE government health portals (**DHA / DHPO
eClaimLink** and **RHA**), parses the eClaim XML into a relational store, and serves
dashboards, scheduled Excel/PDF reports, and a denied-claim resubmission workflow.

Single deployable unit. The same binary runs as: a web app, a background worker
(Hangfire), a Windows Service, and a desktop app (`BIX_DESKTOP=1` → local server +
auto-opened browser).

---

## 2. Repository layout

```
Analytika.sln                  Two projects: Analytika + Analytika.Tests
Analytika/                     The application (everything lives here)
  Program.cs                   Composition root: pipeline, startup maintenance, recurring jobs
  Modules/                     DI registration split into modules; DB provider resolution
  Controllers/                 8 MVC controllers (see §5)
  Models/                      EF entities, AppDbContext, SeedData, ViewModels/
  Services/                    Portal clients, XML parsing, dashboards, reports, jobs
  Security/                    AppRoles constants, credential encryption
  Migrations/                  EF Core migrations — Postgres ONLY (see §4)
  Views/                       Razor views + Shared partials
  wwwroot/                     css/, js/, lib/ (local vendor copies), images/, webfonts/
  appsettings*.json            Base + Development + Production overrides
Analytika.Tests/               xUnit + Moq + FluentAssertions, SQLite-backed
.github/workflows/             dotnet.yml (CI build), release.yml (GHCR + deploy), desktop-release.yml
deploy/, desktop/, scripts/    Windows service, Cloudflare tunnel, installers, ctl scripts
cloudflare-worker/             Small JS Worker proxying to the tunnel origin
mobile/                        Expo/React Native prototype — NOT wired to the backend
docs/, artifacts/, exports/    Static docs and generated output; not build inputs
```

Non-core, safe to ignore unless asked: `mobile/`, `cloudflare-worker/`, `artifacts/`,
`docs/`, `.codex/`, `.cursor/`.

---

## 3. Build, run, test

The .NET 10 SDK is required. Commands below come from CI (`.github/workflows/dotnet.yml`)
and `AGENTS.md`.

```bash
dotnet build Analytika.sln -c Debug          # CI builds Debug and Release
dotnet test  Analytika.sln                   # xUnit suite in Analytika.Tests
dotnet run --project Analytika/Analytika.csproj --no-launch-profile
```

Running locally:

- There is **no `launchSettings.json`** — set the URL explicitly:
  `ASPNETCORE_URLS=http://0.0.0.0:5000`. A `PORT` env var (PaaS) overrides it.
  Conventional ports: mac helper 5000, Windows helper 5200, Docker/prod 8080,
  desktop mode 5097.
- Set `DB_DIR` to a writable folder (e.g. `DB_DIR=/workspace/.devdata`) or the SQLite DB and
  key folders land next to the content root.
- In `Development`, `StartupMaintenance` creates the SQLite schema and seeds data on first
  run. Login: **`admin@ghafbi.ae` / `Admin@123`**.
- Health: `/healthz` (deep check — DB + portal-sync). `/health` is not mapped.
- Helper scripts: `scripts/analytika-ctl.sh|.ps1` (`run|bgrun|pause|resume|restart|stop|status`),
  `start-mac.sh`, `start-windows.bat`, `start.bat`.

**Linting:** there is no linter and no `.editorconfig`. The analyzer-backed build *is* the
check. `dotnet format` reports pre-existing whitespace diffs that CI does not enforce — do
not "fix" them unless asked.

**Expected noise, not bugs:** duplicate-`PackageReference` warnings, NuGet vulnerability
advisories, nullable/EF analyzer warnings, a first-run
`SQLite Error 1: 'no such table: PortalCredentials'` (the credential-encryption upgrade runs
before schema creation), and SMTP/OTEL "not configured" warnings. The build reports
`0 Error(s)` and the app boots fine.

---

## 4. Data layer — the most important convention

The app supports **two providers**, resolved by `Modules/DatabaseConfig.cs`:

| | SQLite (default) | Postgres |
|---|---|---|
| Selected when | nothing else configured | `Database:Provider=postgres`, or `DATABASE_URL` / `ConnectionStrings:Postgres` set |
| Registration | `AddDbContextPool` + `SqlitePragmaInterceptor` | `AddDbContext` + `UseNpgsql` |
| Schema owned by | `Services/SqliteSchemaService.cs` — `EnsureCreated()` + hand-written `ALTER TABLE` / `CREATE TABLE IF NOT EXISTS` | EF Core migrations in `Migrations/`, applied via `Database.Migrate()` on startup |
| Hangfire storage | in-memory | `UsePostgreSqlStorage` (durable) |

> **⚠️ A schema change must be made in BOTH places.** Adding a property to an entity means
> (a) `dotnet ef migrations add <Name>` for Postgres, and (b) a guarded
> `ColumnExists(...)`/`ALTER TABLE` entry in `SqliteSchemaService.MigrateColumns` (or a
> `CREATE TABLE IF NOT EXISTS` in `CreateTables`) for SQLite. Existing SQLite installs are
> upgraded in place — `EnsureCreated()` will **not** add columns to an existing table.
> `Services/XmlParsingService.EnsureSchemaAsync` carries its own SQLite DDL for the
> `XmlParsedRecords`/`XmlParsedActivities` tables — keep it in sync too.

Migrations are generated against Postgres via `Models/DesignTimeDbContextFactory.cs`
(`DATABASE_URL` env var, defaults to `Host=localhost;Database=analytika;Username=postgres`).
`Npgsql.EnableLegacyTimestampBehavior` is on — the codebase uses local-time `DateTime`
throughout, mapped to `timestamp without time zone`.

SQLite runs in **WAL** mode with `busy_timeout=5000` (set in `Program.cs` and the pragma
interceptor) so dashboard reads survive concurrent report writes.

### Domain flow

```
PortalCredential (encrypted)
      │  DhaPortalService / RhaPortalService  (SOAP)
      ▼
PortalTransaction  ──►  PortalFetchLog        raw file metadata + FileContentXml
      │  XmlParsingService / RemittanceParserService
      ▼
XmlParsedRecord (RecordKind = "Submission" | "Remittance")
      └── XmlParsedActivity
      │  ReconciliationService
      ▼
RemittanceClaim  ──►  ResubmissionTask        denial workflow
      │
      ▼
DashboardService (KPIs)   ReportService (Excel via ClosedXML, PDF via itext7)
```

Reference data: `Facility`, `Receiver`, `Payer`, `Clinician`, `Department`, `DhpoCodingSet`.
Users are ASP.NET Identity (`ApplicationUser : IdentityUser`) scoped to facilities via
`UserFacility` and to reports via `UserReportAccess`.

---

## 5. Application structure

### Composition (`Program.cs` → `Modules/ModuleRegistration.cs`)

`AddAnalytikaModules` fans out to `AddCoreModule` (DbContext, Identity, Data Protection,
health checks, response compression, session, resilient portal `HttpClient`s, optional
OpenTelemetry), `AddDashboardModule`, `AddPortalModule`, `AddReportingModule` (currently a
no-op — reporting rides on the existing service graph) and `AddJobsModule`.
**Register new services in the matching module method, not inline in `Program.cs`.**

`Program.cs` itself owns: desktop-mode detection, `DB_DIR` resolution and the
`analytika.db.pending` swap, Serilog, the middleware pipeline, Hangfire recurring-job
definitions, startup schema/seed maintenance, config-validation warnings, and the dashboard
cache pre-warm.

### Middleware order

Serilog request logging → exception handler/HSTS (non-Dev) → response compression → static
files (immutable, 1-year cache) → routing → session → authentication → **security-headers
middleware** → **active-user check** (signs out deactivated users mid-session) →
authorization → optional Hangfire dashboard → MVC default route → `/healthz`.

### Controllers

| Controller | Role gate (`Security/AppRoles.cs`) | Purpose |
|---|---|---|
| `HomeController` | anonymous + authed | Login (`/`), Dashboard, RCMDashboard, Error |
| `PortalController` | `RcmAccess` | Sync, Fetch, SyncedData, XmlParsing, Reconciliation, DataValidation, downloads — the largest controller (~2k lines) |
| `AdminController` | `AdminAccess` | Users, roles, credentials, coding sets, email settings, DB tools |
| `ReportSchedulerController` | `ReportAccess` | One action per report type + submit/download/delete |
| `AdvancedReportsController` | `ReportAccess` | Submission XML file report |
| `ResubmissionController` | `ResubmissionAccess` | Denial dashboard, workload queue, claim detail |
| `AccountController` | authed | Profile |
| `SupportController` | authed | In-app AI support chat (Anthropic API, tightly scoped system prompt) |

Roles: `Admin`, `FacilityAdmin`, `Analyst`, `Billing`, `Finance`, `Auditor`, `Viewer`,
`Reporter`. Use the **composite constants** (`AppRoles.RcmAccess`, `ReportAccess`,
`ResubmissionAccess`, `AdminAccess`) on `[Authorize]`, never string literals.
`Reporter` is facility-scoped and sees reports + support only — the sidebar and
`HomeController` branch on it explicitly.

### Services worth knowing

- **`DhaPortalService`** — DHPO eClaimLink SOAP client. The XML-doc comment at the top of the
  file records the spec's non-obvious rules (attribute-based responses, `dd/MM/yyyy HH:mm:ss`
  dates, 100-day range cap, 500-file result cap, direction/status/transaction-type codes,
  return codes). **Read it before touching portal code.** `SearchTransactionsWithSplitting`
  subdivides date ranges when a chunk saturates at 500.
- **`DashboardService`** — stale-while-revalidate memory cache, 10 min TTL / 2 min soft TTL,
  plus a 15-min cache for dropdown option scans. Pre-warmed on `ApplicationStarted`; cold
  aggregation on a large SQLite DB can take 30–60 s, so never call `WarmAsync` from a request
  path. Eight RCM tabs: Submissions, Resubmissions, Remittance, Denials, Clinicians,
  Operations, Insurance, Department.
- **`ReportService`** — queues and generates reports into `wwwroot/reports` (served as
  `/reports/<file>`); Excel via ClosedXML, PDF via itext7.
- **`XmlParsingService` / `RemittanceParserService` / `ReconciliationService`** — eClaim XML →
  entities, and matching submissions against remittances.
- **State singletons** (`ActiveSyncState`, `PendingDownloadState`, `ReportGenerationState`) —
  in-process progress for the AJAX status endpoints; not durable across restarts.
- **`BixKeepaliveService`** — hits `/healthz` internally to keep the Cloudflare tunnel warm.
  Disable with `Keepalive:Enabled=false`.

---

## 6. Security conventions

- **Portal passwords are encrypted at rest** with ASP.NET Data Protection via
  `Security/CredentialProtector` (`dpv1:` prefix; legacy Base64 values are read and upgraded
  on startup). Never store or log a plaintext portal password; always go through
  `ICredentialProtector`.
- **Data Protection keys** are persisted to disk beside the DB (`dp-keys/` and
  `dataprotection-keys/`, both gitignored). Losing them means credentials can no longer be
  decrypted.
- **A strict CSP** is emitted for every HTML response in `Program.cs`. It allows only `'self'`
  plus the specific CDN hosts already listed. **Adding a new external script/style/font host
  requires editing that CSP** — otherwise it is silently blocked. No page needs
  `'unsafe-eval'`; keep it that way.
- HTML responses are `no-store`; static assets are `immutable` + cache-busted with
  `asp-append-version="true"`.
- Identity: 8+ chars with upper/lower/digit/non-alphanumeric, 5 failed attempts → 15 min
  lockout, 8-hour sliding cookie. Deactivated users (`IsActive=false`) are signed out by
  middleware on their next request.
- `Portal:AllowInvalidCertificates` defaults to **false** and is an explicit operator opt-out.
  Do not flip it to work around a TLS error.
- Secrets come from environment variables (`ANTHROPIC_API_KEY`, SMTP password, `DATABASE_URL`).
  `.env`, DB files, `dp-keys/`, and `logs/` are gitignored — keep it that way.

---

## 7. Background jobs

Hangfire is **off by default** and gated by three independent flags:

| Setting | Effect |
|---|---|
| `BackgroundJobs:HangfireServerEnabled` | runs the job server |
| `BackgroundJobs:RecurringJobsEnabled` | registers the recurring jobs below |
| `BackgroundJobs:HangfireDashboardEnabled` | mounts `/hangfire` (guarded by `HangfireAuthorizationFilter`) |
| `BackgroundJobs:PendingDownloads:HostedServiceEnabled` | runs `PendingDownloadService` |

Recurring jobs (when enabled): `dha-daily-sync` (2 AM), `remittance-auto-parse`
(every 2 h), `db-nightly-backup` (3 AM), `data-retention` (4 AM).

The `docker-compose.yml` topology is the reference production shape: a **worker** container
with jobs + schema maintenance on, and a **web** container with everything off, both sharing
the `/app/data` volume. The worker starts first and must be healthy before the web app
starts.

---

## 8. Frontend conventions

- Server-rendered Razor. jQuery + Bootstrap 5 + Select2 + flatpickr + DataTables +
  Chart.js/ApexCharts, loaded from CDN in `Views/Shared/_Layout.cshtml`. Local copies live in
  `wwwroot/lib/` and are used by the login page (`Views/Home/Index.cshtml`), the denial
  dashboard, and `_ValidationScriptsPartial`.
- Shared partials: `_Layout`, `_PageHeader`, `_KpiCard`, `_StatusBadge`, `_EmptyState`,
  `_GlassCanvas`. Reuse them rather than hand-rolling markup.
- Theming is client-side via `documentElement.dataset`: `data-theme` (light/dark),
  `data-lang` + `dir` (en/ar, RTL-aware), `data-skin` (`classic`, `obsidian`, `ledger`,
  `aurora`, `fable` — defined in `wwwroot/css/themes.css`). Preferences persist in
  `localStorage` under `analytika-theme`, `analytika-lang`, `analytika-skin`,
  `sidebar_collapsed`.
- `wwwroot/css/site.css` is ~190 KB and hand-maintained; `themes.css` layers skin tokens on
  top via `html[data-skin="X"]` selectors. There is no CSS build step.
- Global JS helpers in `wwwroot/js/site.js`: `showAppLoader()` / `hideAppLoader()` /
  `bixSpinnerMarkup()`.
- Known quirk: the pre-hydration inline script in `_Layout.cshtml` (line ~17) references an
  undeclared `skin` variable inside its `try/catch`, so a saved skin is not restored on page
  load even though the picker writes it to `localStorage`. Worth knowing before "fixing"
  skin behaviour elsewhere.

---

## 9. Configuration reference

Environment variables use the ASP.NET double-underscore form
(`BackgroundJobs__HangfireServerEnabled=false`).

| Key | Notes |
|---|---|
| `DB_DIR` | where `analytika.db`, `dp-keys/`, `dataprotection-keys/` live; `/app/data` in Docker |
| `PORT` / `ASPNETCORE_URLS` | binding; `PORT` wins |
| `BIX_DESKTOP=1` / `--desktop` | desktop mode: per-user data dir, port 5097, auto-open browser |
| `Database:Provider`, `DATABASE_URL`, `ConnectionStrings:Postgres` | provider selection |
| `StartupMaintenance:*` | `RunDatabaseSetupOnStartup`, `CreateIndexesOnStartup`, `SeedDataOnStartup` — **all false in Production** |
| `Smtp:*`, `Alerting:AdminEmails` | email + job-failure notifications (optional) |
| `Anthropic:ApiKey` | in-app support chat |
| `Retention:*`, `Backup:RetentionCount` | data retention and backup pruning |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_SERVICE_NAME` | opt-in OpenTelemetry |
| `Logging:JsonConsole` | structured JSON logs for cloud aggregators |
| `Keepalive:Enabled` | tunnel keepalive heartbeat (default true) |

`appsettings.json` ships a Windows-style `DefaultConnection` path and placeholder SMTP
values — both are overridden in every real deployment. Startup logs a warning for each
unconfigured/placeholder value; those warnings are expected in dev.

---

## 10. Testing conventions

`Analytika.Tests` — xUnit, Moq, FluentAssertions, `Microsoft.AspNetCore.Mvc.Testing`,
mirroring the app's folders (`Controllers/`, `Services/`, `Security/`).

- DB-backed tests open an in-memory **SQLite** connection (`Microsoft.Data.Sqlite`) and build
  a real `AppDbContext` — not the EF in-memory provider — so raw-SQL schema code is exercised.
- Controller tests mock `UserManager`/`SignInManager` with the full constructor-argument
  dance (see `HomeControllerTests`) and inject a real `MemoryCache`.
- Private static helpers are reached via reflection (`DashboardServiceTests` on `FormatAed` /
  `FormatDelta`) — follow the existing pattern rather than widening visibility.
- Assertion style is mixed (`Assert.*` and FluentAssertions); match the surrounding file.

CI (`dotnet.yml`) only builds `Analytika/Analytika.csproj` in Debug and Release — it does
**not** run the tests. Run `dotnet test Analytika.sln` yourself before pushing.

---

## 11. Deployment surfaces

| Target | Entry point |
|---|---|
| Docker / GHCR | `Dockerfile` (framework-dependent, arm64+x64), pushed by `.github/workflows/release.yml` on every `main` push |
| Docker Compose | `docker-compose.yml` (worker + web), `docker-compose.cloudflared.yml` |
| Railway / Render / Azure | `railway.toml`, `render.yaml`, `deploy/azure-setup.sh` (opt-in via repo variables `RENDER_DEPLOY_ENABLED` / `RAILWAY_DEPLOY_ENABLED` / `AZURE_DEPLOY_ENABLED`) |
| Windows Server | `deploy/1_publish.ps1` → `2_install_service.ps1`, Cloudflare tunnel scripts, `watchtower.ps1` |
| Desktop installers | `.github/workflows/desktop-release.yml` — tag `desktop-v*`; WiX MSI + macOS `.pkg`/`.dmg`. Cannot be built from Linux. |

Full instructions: `DEPLOY.md`, `deploy/*.md`, `desktop/README.md`.

---

## 12. Working agreements

- **Match the surrounding style.** The codebase uses file-scoped namespaces, constructor
  injection with `private readonly` fields, `ImplicitUsings`, `Nullable` enabled, and box-
  drawing comment banners (`// ── Section ──`) to separate regions in long files. Comments
  explain *why* (spec quirks, perf trade-offs), not *what*.
- **Don't reformat untouched code** and don't run `dotnet format` across the repo.
- **Schema changes touch two code paths** (§4). Verify both.
- **New external hosts require a CSP edit** (§6).
- **Register services in `ModuleRegistration`**, keep `Program.cs` for pipeline/startup only.
- **Portal integrations are external and off by default.** Do not attempt live DHA/RHA calls
  in dev or tests; the spec notes in `IDhaPortalService` / `DhaPortalService` are the source
  of truth for expected shapes.
- **`mobile/` runs on local sample data and is not wired to the backend** — changes there
  don't affect the product.
- The manual QA checklist for authenticated flows lives in `.cursor/agents/test-post-login.md`
  and is a useful smoke-test script after touching dashboards, portal, or reports.

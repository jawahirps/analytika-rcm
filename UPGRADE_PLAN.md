# Analytika RCM — Comprehensive Upgrade Plan

## Executive Summary

Analytika is a **.NET 10 ASP.NET Core MVC** healthcare Revenue Cycle Management analytics application for the UAE market. The codebase is well-structured with modern patterns (minimal APIs avoided in favor of controllers, proper DI, tenant isolation, encrypted credentials, background jobs via Hangfire, OpenTelemetry-ready). This plan identifies modernization opportunities across security, performance, maintainability, observability, and developer experience.

---

## 1. Solution Structure & Project Layout

```
Analytika.sln
├── Analytika/                    # Core MVC application (net10.0)
│   ├── Controllers/              # 10 controllers (Admin, Portal, Home, AI, etc.)
│   ├── Services/                 # 25+ service classes
│   ├── Models/                   # 25+ entity models + ViewModels
│   ├── Security/                 # TenantContext, FacilityScope, CredentialProtector
│   ├── Modules/                  # DI module registration
│   ├── Migrations/               # EF Core migrations (PostgreSQL)
│   └── wwwroot/                  # Static assets, portal downloads
├── Analytika.Tests/              # xUnit test suite (45 tests, SQLite in-memory)
└── tools/                        # CLI utilities (ParseAll, DownloadAll, etc.)
```

**Finding:** Clean separation of concerns. The `Modules/` pattern for DI registration is excellent. No `mobile/` backend coupling (Expo prototype is standalone).

---

## 2. Current .NET Version, Dependencies & Package References

### Target Framework
- **net10.0** (current, supported until Nov 2026 — plan for .NET 11 LTS migration)

### Key Dependencies (Analytika.csproj)
| Package | Version | Notes |
|---------|---------|-------|
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.5 | ✅ Current |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.5 | ✅ Current |
| Microsoft.EntityFrameworkCore.Tools | 10.0.5 | ✅ Current |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 | ✅ Current |
| Hangfire.AspNetCore | 1.8.14 | ⚠️ 1.8.x is maintenance; 2.0 available |
| Hangfire.InMemory | 0.8.0 | ⚠️ Very old; consider `Hangfire.Core` + `Hangfire.MemoryStorage` |
| Hangfire.PostgreSql | 1.20.10 | ⚠️ Sync with Hangfire.Core version |
| OpenTelemetry.* | 1.12.0 | ⚠️ 1.13+ available with better stability |
| ClosedXML | 0.102.2 | ✅ Current |
| itext7 | 8.0.4 | ✅ Current |
| System.IO.Packaging | 10.0.5 | ✅ Pinned for CVE mitigation |
| Microsoft.Extensions.Http.Resilience | 10.7.0 | ✅ Current (Polly v8) |
| Serilog.AspNetCore | 10.0.0 | ⚠️ 10.1+ available |

### Test Dependencies (Analytika.Tests.csproj)
- Uses wildcard versions (`17.*`, `2.*`, `4.*`, `8.*`) — **risk of unexpected upgrades**
- Should pin to specific versions for reproducibility

---

## 3. Deprecated APIs, Security Vulnerabilities & Outdated Patterns

### Critical Security Issues

| Issue | Location | Severity | Remediation |
|-------|----------|----------|-------------|
| **Wildcard package versions in tests** | `Analytika.Tests.csproj:9-16` | 🔴 High | Pin all versions |
| **Hangfire 1.x (EOL)** | `Analytika.csproj:27-29` | 🟡 Medium | Migrate to Hangfire 2.x |
| **`AllowInvalidCertificates` option** | `ModuleRegistration.cs:148-154` | 🟡 Medium | Remove or audit usage; never enable in prod |
| **In-memory Hangfire storage for prod-like workloads** | `ModuleRegistration.cs:256-258` | 🟡 Medium | Use PostgreSQL storage when recurring jobs enabled |
| **Hardcoded fallback passwords in SeedData** | `SeedData.cs:193-202` | 🟡 Medium | Require config values; fail startup if missing |
| **No CSP `script-src-attr` / `style-src-attr`** | `Program.cs:227-241` | 🟢 Low | Add for stricter CSP |

### Deprecated / Suboptimal Patterns

| Pattern | Location | Recommendation |
|---------|----------|----------------|
| `Task.Run(() => ...)` fire-and-forget in controllers/services | `DashboardService.cs:47,56,124` | Use `IHostedService` / `BackgroundService` or `IAsyncEnumerable` streaming |
| Manual `SqlitePragmaInterceptor` for WAL/busy_timeout | `Modules/SqlitePragmaInterceptor.cs` | Use `Microsoft.Data.Sqlite` connection string options (`Pooling=True;Default Timeout=120;Cache=Shared`) |
| Raw SQL in `SqliteSchemaService.EnsureSchema` | `Services/SqliteSchemaService.cs` | Use EF Core migrations for SQLite too (or `EnsureCreatedAsync`) |
| `System.Text.Json` serialization of snapshots to disk | `DashboardService.cs:93,103` | Consider `DistributedCache` or Redis for multi-instance |
| `IHttpClientFactory` with `Timeout.InfiniteTimeSpan` | `ModuleRegistration.cs:157` | Set explicit timeouts; rely on resilience pipeline |
| No `IAsyncDisposable` on services holding unmanaged resources | Multiple services | Implement where applicable |

### CVEs to Monitor
- `System.IO.Packaging 6.0.0` — already pinned to 10.0.5 ✅
- Regular `dotnet list package --vulnerable --include-transitive` in CI recommended

---

## 4. Database Schema & Entity Framework Usage

### Schema Overview (29 tables)
**Core Identity:** `AspNetUsers`, `AspNetRoles`, `AspNetUserClaims`, `AspNetUserRoles`, `AspNetUserLogins`, `AspNetUserTokens`, `AspNetRoleClaims`

**Business Entities:**
- `Facilities` (multi-tenant clinics)
- `PortalCredentials` (encrypted DHA/RHA credentials)
- `PortalTransactions` (40GB+ table, raw portal API responses)
- `XmlParsedRecords` / `XmlParsedActivities` (parsed claim/remittance data)
- `RemittanceClaims` / `ResubmissionTasks` (reconciliation workflow)
- `ReportRequests` / `ReportSchedules` (reporting engine)
- `Tenants` / `UserFacilities` / `UserReportAccesses` (tenant isolation)
- `DhpoCodingSets`, `Payers`, `Receivers`, `Clinicians`, `Departments` (lookups)
- `SystemSettings`, `PortalFetchLogs`, `AiUsageLogs`

### EF Core Usage Assessment

| Aspect | Status | Notes |
|--------|--------|-------|
| **Migrations** | PostgreSQL only | SQLite uses raw SQL (`SqliteSchemaService`) — inconsistent |
| **Change Tracking** | Mixed | Good use of `AsNoTracking()` in read-heavy services |
| **Bulk Operations** | Manual | `ExecuteUpdateAsync`/`ExecuteDeleteAsync` used in seeds; consider `EFCore.BulkExtensions` |
| **Connection Resilience** | Basic | SQLite `Default Timeout=120`; Postgres needs `EnableRetryOnFailure()` |
| **Query Performance** | Good | Strategic indexes; some N+1 risks in controllers (e.g., `PortalController`) |
| **Concurrency** | Optimistic | No `ConcurrencyToken` on high-contention entities |

### Schema Modernization Opportunities
1. **Unify SQLite/PostgreSQL schema management** — use EF migrations for both
2. **Add computed columns** for common aggregations (e.g., `XmlParsedRecords.GrossAmount`)
3. **Partition `PortalTransactions`** by date (PostgreSQL native partitioning)
4. **Add soft-delete** (`IsDeleted` + query filters) instead of hard deletes
5. **Consider temporal tables** (PostgreSQL) for audit trail on `PortalCredentials`, `RemittanceClaims`

---

## 5. Authentication & Authorization Implementation

### Current Implementation
- **ASP.NET Core Identity** with `ApplicationUser` (custom properties: `FullName`, `Department`, `TenantId`, `UserType`, `IsActive`)
- **Role-based**: Admin, FacilityAdmin, Analyst, Billing, Finance, Auditor, Viewer, Reporter
- **Tenant isolation**: `ITenantContext` + `FacilityScopeService` — **excellent design**
- **Credential encryption**: `ICredentialProtector` using ASP.NET Data Protection — **well done**
- **Rate limiting** on auth endpoints: 10 req/15min per IP — **good**
- **Cookie auth**: 8hr sliding expiration, secure flags — **standard**

### Gaps & Improvements

| Area | Current | Recommended |
|------|---------|-------------|
| **MFA/2FA** | Not implemented | Add TOTP (Google Authenticator) for Admin/FacilityAdmin |
| **Passwordless** | No | Consider WebAuthn / magic links for facility reporters |
| **Session management** | In-memory | Distributed cache (Redis) for multi-instance |
| **Audit logging** | Minimal | Structured audit trail for credential access, role changes |
| **API authentication** | Cookie only | Add JWT Bearer for mobile/API clients (future-proofing) |
| **Permission granularity** | Role + facility | Consider policy-based auth (`IAuthorizationHandler`) for complex rules |
| **Device tracking** | No | Track trusted devices, allow revocation |

### Specific Code Issues
- `HomeController.Login()`: `FindByEmailAsync` then `PasswordSignInAsync` — **use `SignInManager.PasswordSignInAsync` with email directly** (supported in .NET 8+)
- `Program.cs:249-266` middleware checks `user.IsActive` on every request — **move to `SecurityStampValidator`** for efficiency
- No **refresh token rotation** for long-lived sessions

---

## 6. Test Suite & CI Configuration

### Test Coverage (Analytika.Tests)
| Category | Tests | Coverage |
|----------|-------|----------|
| Controllers | 1 (`HomeControllerTests`) | Minimal |
| Services | 6 (Dashboard, Reconciliation, NvidiaAnalyst, ReportDateWindow, SqliteSchema, SyncHealthCheck, XmlParsing) | Partial |
| Security | 2 (HangfireAuth, PasswordPolicy) | Good for policies |
| **Total** | **~45 tests** | **~30% estimated** |

### CI Pipeline (`.github/workflows/dotnet.yml`)
```yaml
# Current: Build only (Debug + Release)
# Missing: Test, Security Scan, Container Build, Deploy Gates
```

### Critical CI Gaps
1. **No test execution** in CI — `dotnet test` not run
2. **No security scanning** — `dotnet list package --vulnerable`, Trivy, CodeQL
3. **No container build validation** — Dockerfile not tested
4. **No migration validation** — `dotnet ef migrations list` / SQL script generation
5. **No performance benchmarks** — regression detection

---

## 7. Areas for Modernization

### Performance
| Area | Current | Target | Effort |
|------|---------|--------|--------|
| **Dashboard aggregation** | 20-60s cold, cached 10min | <5s cold via materialized views / OLAP | Medium |
| **XML parsing** | Sequential, in-memory | Streaming + parallel (already partially done) | Low |
| **Report generation** | Sync, blocks thread | Background job + SignalR progress | Medium |
| **DB connection pooling** | SQLite pooled, PG default | Tune `MaxPoolSize`, `MinPoolSize` | Low |
| **Static assets** | No CDN, cache-busting only | Cloudflare R2 / Azure Blob + CDN | Medium |
| **Response compression** | Brotli/Gzip Fastest | Zstd (if .NET 10+ supports) | Low |

### Security
| Area | Priority | Tasks |
|------|----------|-------|
| **Dependency scanning** | 🔴 Critical | Add to CI; automate Dependabot |
| **Secret scanning** | 🔴 Critical | GitHub Secret Scanning + TruffleHog |
| **SAST** | 🟡 High | CodeQL / SonarCloud |
| **Container hardening** | 🟡 High | Distroless base, non-root, read-only fs |
| **Penetration testing** | 🟢 Medium | Annual third-party |

### Maintainability
| Area | Current | Target |
|------|---------|--------|
| **Code organization** | Controllers up to 100KB (`PortalController` 102KB) | Feature folders / vertical slices |
| **API layer** | MVC controllers returning views | Add minimal API endpoints for AJAX/SPA |
| **Documentation** | XML comments sparse | OpenAPI/Swagger + `///` docs on public APIs |
| **Database docs** | None | ER diagram, data dictionary |
| **Architecture tests** | None | NetArchTest / ArchUnitNET for layer rules |

### Observability
| Component | Current | Gap |
|-----------|---------|-----|
| **Logs** | Serilog → Console + File | Structured JSON, correlation IDs, Loki/Grafana |
| **Metrics** | OpenTelemetry (opt-in) | Prometheus scrape endpoint, Grafana dashboards |
| **Traces** | OpenTelemetry (opt-in) | W3C trace-context, Jaeger/Tempo |
| **Health checks** | `/healthz` (DB + portal-sync) | Add: disk space, memory, external deps (SMTP, portals) |
| **Profiling** | None | Continuous profiling (Pyroscope/Grafana) |

### Developer Experience
- **No `launchSettings.json`** — relies on `ASPNETCORE_URLS` / Kestrel config
- **No dev container** — add `.devcontainer/` for Codespaces
- **No local HTTPS** — `dotnet dev-certs https --trust` not documented
- **Database tooling** — `dotnet ef` works but no SQLite GUI recommendation

---

## 8. Phased Upgrade Plan

### Phase 0: Foundation (Week 1-2) — **Do First**
| Task | Description | Files/Commands |
|------|-------------|----------------|
| **Pin test dependencies** | Replace wildcards in `Analytika.Tests.csproj` with exact versions | `dotnet add package --version` |
| **Enable test execution in CI** | Add `dotnet test Analytika.sln --no-build -c Release` to `dotnet.yml` | `.github/workflows/dotnet.yml` |
| **Add vulnerability scanning** | `dotnet list package --vulnerable --include-transitive` in CI | `.github/workflows/dotnet.yml` |
| **Add CodeQL analysis** | GitHub Advanced Security or free for public repos | `.github/workflows/codeql.yml` |
| **Container build validation** | Build Dockerfile in CI, run Trivy scan | `.github/workflows/docker.yml` |
| **Fail on warnings** | `TreatWarningsAsErrors` for CI builds | `Directory.Build.props` |

### Phase 1: Security Hardening (Week 2-4)
| Task | Priority | Description |
|------|----------|-------------|
| **Remove `AllowInvalidCertificates`** | 🔴 | Delete config + code path; enforce valid TLS |
| **Migrate Hangfire to 2.x** | 🟡 | Update packages, fix breaking changes (storage, DI) |
| **Add MFA for Admin roles** | 🟡 | `AddTwoFactorAuthenticator()`, QR code setup |
| **Implement `SecurityStampValidator`** | 🟡 | Replace per-request `IsActive` check in middleware |
| **Add audit logging** | 🟡 | `Audit.NET` or custom `IAuditService` for credential/role changes |
| **Secrets rotation** | 🟢 | Document rotation procedure for `DataProtection`, `SMTP`, `Anthropic` keys |

### Phase 2: Database & EF Core Modernization (Week 4-6)
| Task | Priority | Description |
|------|----------|-------------|
| **Unify SQLite migrations** | 🟡 | Generate SQLite migration from model; remove `SqliteSchemaService` raw SQL |
| **Add `EnableRetryOnFailure` for Postgres** | 🟡 | `options.UseNpgsql(conn, o => o.EnableRetryOnFailure())` |
| **Add concurrency tokens** | 🟢 | `ConcurrencyToken` on `PortalCredentials`, `RemittanceClaims` |
| **Implement soft delete** | 🟢 | Global query filter `IsDeleted == false` |
| **Add computed columns / indexed views** | 🟢 | For dashboard aggregates (PostgreSQL materialized views) |
| **Bulk extensions evaluation** | 🟢 | Test `EFCore.BulkExtensions` for report generation, backfills |

### Phase 3: Performance & Scalability (Week 6-10)
| Task | Priority | Description |
|------|----------|-------------|
| **Dashboard materialized view** | 🟡 | PostgreSQL: `REFRESH MATERIALIZED VIEW CONCURRENTLY` every 5min |
| **Report generation → background** | 🟡 | Hangfire job + SignalR progress; return `ReportRequest` immediately |
| **Add Redis for distributed cache/session** | 🟡 | `IDistributedCache`, `AddStackExchangeRedisCache` |
| **CDN for static assets** | 🟢 | Cloudflare R2 / Azure Blob + Cloudflare CDN |
| **Connection pool tuning** | 🟢 | `MinPoolSize=5`, `MaxPoolSize=100` based on load test |
| **Enable Zstd compression** | 🟢 | If .NET 10 supports; else Brotli is fine |

### Phase 4: Observability & Operations (Week 8-12)
| Task | Priority | Description |
|------|----------|-------------|
| **Structured JSON logging** | 🟡 | Serilog `JsonFormatter`, correlation IDs (`HttpContext.TraceIdentifier`) |
| **Prometheus metrics endpoint** | 🟡 | `OpenTelemetry.Prometheus` exporter + `/metrics` |
| **Grafana dashboards** | 🟡 | Request latency, error rate, DB pool, Hangfire queue, cache hit rate |
| **Distributed tracing** | 🟡 | OTLP → Tempo/Jaeger; ensure `traceparent` propagation |
| **Enhanced health checks** | 🟢 | Disk, memory, SMTP, portal connectivity, migration status |
| **Continuous profiling** | 🟢 | Pyroscope / Grafana Profiler integration |

### Phase 5: Architecture & Developer Experience (Week 10-16)
| Task | Priority | Description |
|------|----------|-------------|
| **Vertical slice architecture** | 🟢 | Group by feature: `Features/Portal`, `Features/Reports`, `Features/AI` |
| **Minimal API endpoints** | 🟢 | Add `MapGet/MapPost` for AJAX calls; keep MVC for pages |
| **OpenAPI/Swagger** | 🟢 | `Swashbuckle.AspNetCore` + XML docs |
| **Architecture tests** | 🟢 | `NetArchTest` — enforce layer rules (Controllers → Services → Models) |
| **Dev container** | 🟢 | `.devcontainer/devcontainer.json` with .NET 10, SQLite, PostgreSQL |
| **Local HTTPS docs** | 🟢 | Document `dotnet dev-certs https --trust` |
| **Database documentation** | 🟢 | `SchemaSpy` or `dbdocs.io` for ER diagrams |

### Phase 6: .NET 11 LTS Migration (Post-Nov 2026)
| Task | Description |
|------|-------------|
| **Update TFM** | `<TargetFramework>net11.0</TargetFramework>` |
| **Update all packages** | `dotnet outdated --upgrade` |
| **Test thoroughly** | Run full test suite, migration validation, load test |
| **Update Docker base images** | `mcr.microsoft.com/dotnet/sdk:11.0`, `aspnet:11.0` |
| **Leverage new features** | `System.Text.Json` improvements, `TimeProvider`, `HybridCache` |

---

## 9. Risk Assessment & Mitigation

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| **Hangfire 2.x breaking changes** | High | Medium | Test in staging; maintain in-memory fallback |
| **SQLite migration conflicts** | Medium | High | Generate migration from current model; test on copy of prod DB |
| **Redis session migration** | Low | Medium | Blue-green deploy; sticky sessions during transition |
| **CSP breakage** | Medium | Low | Report-only mode first (`Content-Security-Policy-Report-Only`) |
| **Dependency upgrade regressions** | Medium | Medium | Pin versions; automated tests; Dependabot PRs with auto-merge on green |

---

## 10. Quick Wins (Can Do This Week)

1. **Pin test package versions** — 30 min
2. **Add `dotnet test` to CI** — 15 min
3. **Add vulnerability scan to CI** — 15 min
4. **Remove `AllowInvalidCertificates`** — 1 hour (verify no prod usage)
5. **Add `TreatWarningsAsErrors` for CI** — 15 min
6. **Document local HTTPS setup** — 30 min
7. **Add `.editorconfig`** for consistent formatting — 30 min

---

## 11. Recommended Tooling Additions

| Tool | Purpose | Integration |
|------|---------|-------------|
| **Dependabot** | Automated dependency PRs | `.github/dependabot.yml` |
| **CodeQL** | Static analysis | GitHub Actions (free) |
| **Trivy** | Container vulnerability scan | CI step |
| **NetArchTest** | Architecture enforcement | Unit test project |
| **SchemaSpy** | DB documentation | CI artifact |
| **BenchmarkDotNet** | Performance regression | Dedicated benchmarks project |
| **Aspire** (future) | Orchestration, service discovery | .NET 9+ |

---

## 12. Conclusion

Analytika is a **well-architected, production-ready application** with thoughtful domain modeling (tenant isolation, credential encryption, background job resilience). The upgrade plan prioritizes:

1. **Security hygiene** (CI scanning, dependency pinning, cert validation)
2. **Operational maturity** (testing in CI, observability, health checks)
3. **Performance at scale** (dashboard materialization, background reports, caching)
4. **Long-term maintainability** (architecture tests, vertical slices, .NET 11 migration)

**Estimated effort:** 16 weeks for full plan (Phases 0-5), with Phase 0-1 deliverable in 2-4 weeks for immediate risk reduction.

---

*Generated: 2026-08-09 | Analytika RCM vNext Upgrade Plan*
using Analytika.Models;
using Analytika.Modules;
using Analytika.Services;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Allow running as a Windows Service (sc.exe / NSSM) without a console.
builder.Host.UseWindowsService();

// Serilog: reads sinks/levels from the "Serilog" section of appsettings.
builder.Services.AddSerilog((services, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services));
// UseSerilogRequestLogging() depends on DiagnosticContext — register it explicitly
// (the AddSerilog overload above does not register it in Serilog.AspNetCore 10.0.0).
builder.Services.AddSingleton<Serilog.Extensions.Hosting.DiagnosticContext>(
    _ => new Serilog.Extensions.Hosting.DiagnosticContext(null));
builder.Services.AddSingleton<Serilog.IDiagnosticContext>(
    sp => sp.GetRequiredService<Serilog.Extensions.Hosting.DiagnosticContext>());

// Structured JSON logs for cloud log aggregators (opt-in: Logging__JsonConsole=true)
if (builder.Configuration.GetValue("Logging:JsonConsole", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    });
}

var hangfireServerEnabled = builder.Configuration.GetValue("BackgroundJobs:HangfireServerEnabled", false);
var recurringJobsEnabled = builder.Configuration.GetValue("BackgroundJobs:RecurringJobsEnabled", false);
var hangfireDashboardEnabled = builder.Configuration.GetValue("BackgroundJobs:HangfireDashboardEnabled", false);

// Allow large DB uploads (up to 3 GB) via the migration endpoint
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 3L * 1024 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

// In Docker the DB lives in /app/data (mounted volume); locally stays beside the app
var dataDir = Environment.GetEnvironmentVariable("DB_DIR")
    ?? builder.Environment.ContentRootPath;
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "analytika.db");

// If a pending DB was uploaded via the migration endpoint, swap it in now (before EF opens the file)
var pendingDb = dbPath + ".pending";
if (System.IO.File.Exists(pendingDb))
{
    if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath);
    System.IO.File.Move(pendingDb, dbPath);
}
// Persist Data Protection keys with the DB so encrypted credentials survive restarts/redeploys
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dataprotection-keys")))
    .SetApplicationName("Analytika");

builder.Services.AddAnalytikaModules(
    builder.Configuration,
    dbPath,
    hangfireServerEnabled,
    recurringJobsEnabled,
    builder.Configuration.GetValue("BackgroundJobs:PendingDownloads:HostedServiceEnabled", false));

// Bix keepalive — internal /healthz heartbeat keeps Cloudflare tunnel & request pipeline warm.
// Disable by setting Keepalive:Enabled=false in appsettings.
if (builder.Configuration.GetValue("Keepalive:Enabled", true))
{
    builder.Services.AddHttpClient("bix-keepalive");
    builder.Services.AddHostedService<Analytika.Services.BixKeepaliveService>();
}

// Warm-up — periodically rebuilds the heavy dashboard aggregation cache and keeps the
// SQLite hot-page cache resident, so the app stays fast after idle (not only while actively
// used). /healthz keepalive alone does no DB work. Disable via Warmup:Enabled=false.
if (builder.Configuration.GetValue("Warmup:Enabled", true))
    builder.Services.AddHostedService<Analytika.Services.WarmupHostedService>();

// Respect PORT env variable (set by preview/hosting environment)
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseSerilogRequestLogging(o =>
{
    // /healthz is pinged every 20-60s by the keepalive + health checks — logging each
    // one is pure churn (thousands of identical lines/day). Log it only when unhealthy.
    o.GetLevel = (ctx, _, ex) =>
        ex != null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error :
        ctx.Request.Path.StartsWithSegments("/healthz") ? Serilog.Events.LogEventLevel.Verbose :
        Serilog.Events.LogEventLevel.Information;
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Static assets are cache-busted via asp-append-version, so they can be
        // cached aggressively. (HTML responses stay no-store via the security middleware.)
        ctx.Context.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
    }
});
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        var isHtml = string.Equals(context.Response.ContentType?.Split(';', 2)[0], "text/html", StringComparison.OrdinalIgnoreCase);
        if (isHtml)
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            headers["Pragma"] = "no-cache";
            headers["Expires"] = "0";

            // The public front-of-house pages (marketing landing at "/" and the login
            // form at /login, /Home/Index, /Home/Login) are unauthenticated and carry no
            // PHI. Scope any script relaxation to those routes only — every
            // authenticated/PHI page keeps the strict policy.
            var path = context.Request.Path.Value ?? string.Empty;
            var isLogin = context.User.Identity?.IsAuthenticated != true
                && (path == "/"
                    || path.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/Home/Index", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/Home/Login", StringComparison.OrdinalIgnoreCase));
            var scriptEval = isLogin ? "'unsafe-eval' " : "";
            var splineHosts = isLogin ? " https://unpkg.com" : "";

            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "base-uri 'self'; " +
                "frame-ancestors 'none'; " +
                "form-action 'self'; " +
                "object-src 'none'; " +
                "img-src 'self' data: https: blob:; " +
                "media-src 'self' data: blob:; " +
                "font-src 'self' https://fonts.gstatic.com https://cdnjs.cloudflare.com data:; " +
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://cdn.datatables.net; " +
                "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' " + scriptEval +
                    "https://cdn.jsdelivr.net https://code.jquery.com https://cdnjs.cloudflare.com https://cdn.datatables.net" + splineHosts + "; " +
                "worker-src 'self' blob:; " +
                "connect-src 'self' https:; " +
                "frame-src 'none'";
        }

        return Task.CompletedTask;
    });

    await next();
});
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(context.User);

        if (user == null || !user.IsActive)
        {
            await signInManager.SignOutAsync();
            context.Response.Redirect("/Home/Index");
            return;
        }
    }

    await next();
});
app.UseAuthorization();

if (hangfireDashboardEnabled)
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter() }
    });

if (hangfireServerEnabled || recurringJobsEnabled)
    GlobalJobFilters.Filters.Add(new JobFailureNotificationFilter(app.Services));

if (recurringJobsEnabled)
{
    // Resolve via DI (not the static RecurringJob API) so Hangfire storage is
    // initialized first — the static API throws when the dashboard is disabled.
    using var jobScope = app.Services.CreateScope();
    var recurringJobs = jobScope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    // Daily cron: sync last 90 days of DHA transactions for all active credentials (2 AM)
    recurringJobs.AddOrUpdate<PortalSyncService>(
        "dha-daily-sync",
        svc => svc.RunDailyDhaSyncAsync(),
        Cron.Daily(2));

    // Every 2 hours: parse any remittance XMLs not yet turned into claims (uses stored FileContentXml — no portal request)
    recurringJobs.AddOrUpdate<RemittanceParserService>(
        "remittance-auto-parse",
        svc => svc.ParsePendingAsync(null),
        "0 */2 * * *");

    // Nightly DB backup (3 AM — after the 2 AM sync) and data retention (4 AM)
    RecurringJob.AddOrUpdate<DatabaseMaintenanceService>(
        "db-nightly-backup",
        svc => svc.BackupDatabaseAsync(),
        Cron.Daily(3));

    RecurringJob.AddOrUpdate<DatabaseMaintenanceService>(
        "data-retention",
        svc => svc.RunRetentionAsync(),
        Cron.Daily(4));
}
else
{
    // Hangfire is in-memory here and the server is disabled, so there is no recurring
    // work to clean up on startup. Skipping storage calls keeps local startup light.
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Liveness/readiness probe for hosting platforms (checks DB connectivity)
app.MapHealthChecks("/healthz").AllowAnonymous();

using (var startupScope = app.Services.CreateScope())
{
    var startupServices = startupScope.ServiceProvider;
    var startupDb = startupServices.GetRequiredService<AppDbContext>();

    // WAL mode lets reads proceed concurrently with the background report writer;
    // busy_timeout gives short transactions up to 5 s to acquire the lock instead
    // of immediately returning SQLITE_BUSY while a report generation is running.
    if (!startupDb.Database.IsNpgsql())
    {
        startupDb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");
        startupDb.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000");
    }

    if (startupDb.Database.IsNpgsql())
    {
        // Postgres schema is owned by EF migrations — always bring it current
        startupDb.Database.Migrate();
        if (app.Configuration.GetValue("StartupMaintenance:SeedDataOnStartup", false))
            await SeedData.InitializeAsync(startupServices);
    }

    // One-time upgrade: legacy Base64-stored portal passwords → encrypted at rest
    try
    {
        var protector = startupServices.GetRequiredService<Analytika.Security.ICredentialProtector>();
        var creds = await startupDb.PortalCredentials.ToListAsync();
        var upgraded = 0;
        foreach (var c in creds)
        {
            if (protector.IsProtected(c.PasswordEncrypted)) continue;
            try { c.PasswordEncrypted = protector.Protect(protector.Unprotect(c.PasswordEncrypted)); upgraded++; }
            catch { /* unreadable legacy value — left as-is, surfaced by the credential test */ }
        }
        if (upgraded > 0)
        {
            await startupDb.SaveChangesAsync();
            app.Logger.LogInformation("Upgraded {Count} portal credential(s) to encrypted-at-rest storage", upgraded);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Credential encryption upgrade skipped (database not ready?)");
    }
}

if (app.Configuration.GetValue("StartupMaintenance:RunDatabaseSetupOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDbContext>();
    if (!db.Database.IsNpgsql())
    {
        var createIndexes = app.Configuration.GetValue("StartupMaintenance:CreateIndexesOnStartup", false);
        SqliteSchemaService.EnsureSchema(db, createIndexes);
    }

    if (!db.Database.IsNpgsql() && app.Configuration.GetValue("StartupMaintenance:SeedDataOnStartup", false))
        await SeedData.InitializeAsync(services);
}

// ── Reconcile orphaned report jobs ────────────────────────────────────
// Generation state lives in-memory, so any report mid-generation when the app
// stops is orphaned as "Processing" forever. Mark them Failed on boot so the
// UI is honest and users know to resubmit.
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var stuck = await db.ReportRequests.Where(r => r.Status == "Processing").ToListAsync();
    if (stuck.Count > 0)
    {
        foreach (var r in stuck) r.Status = "Failed";
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Startup reconcile: marked {Count} orphaned 'Processing' report(s) as Failed.", stuck.Count);
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Startup report reconcile skipped (database not ready?)");
}

// ── Startup config validation ─────────────────────────────────────────
{
    var smtpHost = app.Configuration["Smtp:Host"];
    if (string.IsNullOrEmpty(smtpHost) || smtpHost == "smtp.example.com")
        app.Logger.LogWarning("SMTP host is not configured — email delivery will be disabled. Set Smtp:Host in appsettings or environment.");

    var smtpPwd = app.Configuration["Smtp:Password"];
    if (smtpPwd == "YOUR_SMTP_PASSWORD")
        app.Logger.LogWarning("SMTP password is still the placeholder value — email delivery will fail. Set Smtp:Password via environment variable.");

    var adminEmails = app.Configuration["Alerting:AdminEmails"];
    if (string.IsNullOrEmpty(adminEmails))
        app.Logger.LogWarning("Alerting:AdminEmails is empty — job failure notifications will not be sent.");

    var backupRetention = app.Configuration.GetValue("Backup:RetentionCount", 14);
    if (backupRetention < 1)
        app.Logger.LogWarning("Backup:RetentionCount is {Count} — this may delete all backups. Set to at least 1.", backupRetention);

    var guestPwd = app.Configuration["Security:GuestPassword"];
    if (string.IsNullOrEmpty(guestPwd))
        app.Logger.LogWarning("Security:GuestPassword is not configured — guest provisioning will use a default password.");
}

// Pre-warm dashboard facility status so the first user lands on hot data.
// Runs after ApplicationStarted so it never blocks request acceptance.
// Uses a long timeout because the initial aggregation on a large SQLite DB
// can take 30-60 s cold; once it completes the cache has a 10-min TTL with
// stale-while-revalidate so no user ever sees the cold path.
app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = Task.Run(async () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = app.Services.CreateScope();
            var dashboard = scope.ServiceProvider.GetRequiredService<Analytika.Services.IDashboardService>();
            // WarmAsync forces the full aggregation and seeds the cache — subsequent
            // requests hit the cache. BuildFacilityStatusAsync would just return
            // an empty placeholder here.
            await dashboard.WarmAsync();

            // The RCM dashboard's receiver/payer/encounter-type dropdown options are
            // full-table Distinct() scans (~11s cold on a large DB), cached for 15 min.
            // One call here seeds that cache so the first real user never pays it.
            await dashboard.BuildRcmDashboardAsync("Submissions", new Analytika.Models.ViewModels.RcmDashboardFilters());

            sw.Stop();
            app.Logger.LogInformation("Pre-warmed dashboard caches in {Ms} ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            app.Logger.LogWarning(ex, "Dashboard pre-warm failed after {Ms} ms — first user will see cold path",
                sw.ElapsedMilliseconds);
        }
    });
});

app.Run();

using Analytika.Models;
using Analytika.Modules;
using Analytika.Services;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System.Diagnostics;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// Allow running as a Windows Service (sc.exe / NSSM) without a console.
builder.Host.UseWindowsService();

// Desktop app mode: the installed Windows/macOS build launches with BIX_DESKTOP=1
// (or the --desktop arg). It can't write its SQLite DB / DataProtection keys into a
// read-only install location (Program Files / .app), so default DB_DIR to a per-user
// writable folder before anything reads it below.
var isDesktop = Environment.GetEnvironmentVariable("BIX_DESKTOP") == "1"
    || args.Contains("--desktop");
var isReportWorker = args.Contains("--report-worker", StringComparer.OrdinalIgnoreCase);
var isDemo = Environment.GetEnvironmentVariable("BIX_DEMO") == "1";
if ((isDesktop || isDemo) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DB_DIR")))
    Environment.SetEnvironmentVariable("DB_DIR",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), isDemo ? "BixDemo" : "Bix"));

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

// Respect PORT env variable (set by preview/hosting environment)
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Desktop app: bind to a fixed loopback port if the launcher didn't choose one.
if (isDesktop
    && string.IsNullOrEmpty(port)
    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://localhost:5097");

// Demo mode is deliberately loopback-only. It uses an isolated seeded database
// and automatic authentication, and can never expose the bypass on the network.
if (isDemo)
    builder.WebHost.UseUrls("http://127.0.0.1:5098");

var app = builder.Build();

// Desktop app mode: open the default browser once the server is ready.
if (isDesktop)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            var url = (app.Urls.FirstOrDefault() ?? "http://localhost:5097")
                .Replace("://+", "://localhost").Replace("://0.0.0.0", "://localhost");
            ProcessStartInfo psi =
                OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", url)
                : new ProcessStartInfo("xdg-open", url);
            Process.Start(psi);
        }
        catch { /* best-effort browser launch */ }
    });
}

app.UseSerilogRequestLogging();

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
if (isDemo)
{
    app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true &&
            context.Connection.LocalIpAddress != null &&
            System.Net.IPAddress.IsLoopback(context.Connection.LocalIpAddress))
        {
            var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var signInManager = context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
            var demoEmail = app.Configuration["Demo:UserEmail"] ?? "admin@ghafbi.ae";
            var demoUser = await userManager.FindByEmailAsync(demoEmail);
            if (demoUser?.IsActive == true)
            {
                var principal = await signInManager.CreateUserPrincipalAsync(demoUser);
                context.User = principal;
                await context.SignInAsync(
                    IdentityConstants.ApplicationScheme,
                    principal,
                    new AuthenticationProperties { IsPersistent = true });
            }
        }

        await next();
    });
}
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

            // Strict CSP on every page, login included. The login uses a canvas
            // particle background (tsParticles, no eval required), so no page needs
            // 'unsafe-eval' or extra script hosts.
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
                "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' " +
                    "https://cdn.jsdelivr.net https://code.jquery.com https://cdnjs.cloudflare.com https://cdn.datatables.net; " +
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
        // Avoid a user-table query on every CSS, API and page request. A short cache
        // keeps deactivation responsive while taking authentication reads out of the
        // hot path during bursts from dashboard pages.
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var cache = context.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        var cacheKey = $"auth:active:{userId}";
        var isActive = !string.IsNullOrEmpty(userId) && await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var userManager = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(userId!);
            return user?.IsActive == true;
        });

        if (!isActive)
        {
            var signInManager = context.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
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

// Public liveness and deployment identity endpoint. Keep this independent of
// database and portal readiness so supervisors only restart a dead web process.
app.MapGet("/api/health", (HttpContext context) =>
{
    var assembly = Assembly.GetExecutingAssembly();
    var informationalVersion = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "unknown";
    var version = assembly.GetName().Version?.ToString() ?? "unknown";
    var revisionFromVersion = informationalVersion.Contains('+')
        ? informationalVersion[(informationalVersion.LastIndexOf('+') + 1)..]
        : null;
    var commitSha = Environment.GetEnvironmentVariable("BIX_COMMIT_SHA")
        ?? revisionFromVersion
        ?? "unknown";

    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new
    {
        status = "ok",
        service = "Bix Analytika RCM",
        version,
        informationalVersion,
        commitSha,
        environment = app.Environment.EnvironmentName,
        startedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        timestampUtc = DateTimeOffset.UtcNow
    });
}).AllowAnonymous();

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

if (isReportWorker)
{
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    await ExternalReportWorker.RunAsync(app.Services, app.Configuration, app.Logger, shutdown.Token);
    return;
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

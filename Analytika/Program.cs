using Analytika.Models;
using Analytika.Services;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Allow large DB uploads (up to 3 GB) via the migration endpoint
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 3L * 1024 * 1024 * 1024);

// ── Performance: response compression ──────────────────────────────────────
builder.Services.AddResponseCompression(opts =>
{
    opts.EnableForHttps = true;
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    opts.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    opts.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "text/html", "application/json", "text/css", "application/javascript", "image/svg+xml" });
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);

// ── Performance: response caching ─────────────────────────────────────────
builder.Services.AddResponseCaching();

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
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Home/Index";
    options.LogoutPath = "/Home/LogOut";
    options.AccessDeniedPath = "/Home/Index";
});

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage());

builder.Services.AddHangfireServer();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IPowerBIService, PowerBIService>();
builder.Services.AddScoped<IDhaPortalService, DhaPortalService>();
builder.Services.AddScoped<IRhaPortalService, RhaPortalService>();
builder.Services.AddScoped<PortalSyncService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<RemittanceParserService>();
builder.Services.AddHostedService<PendingDownloadService>();
builder.Services.AddHttpClient("DHA").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });
builder.Services.AddHttpClient("RHA").ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });
builder.Services.AddMemoryCache();
builder.Services.AddControllersWithViews();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// Respect PORT env variable (set by preview/hosting environment)
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseResponseCompression();
app.UseResponseCaching();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Cache static assets for 1 year (CSS/JS have cache-busting query strings via asp-append-version)
        var headers = ctx.Context.Response.GetTypedHeaders();
        headers.CacheControl = new Microsoft.Net.Http.Headers.CacheControlHeaderValue
        {
            Public = true,
            MaxAge = TimeSpan.FromDays(365)
        };
    }
});
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire");

// Daily cron: sync last 90 days of DHA transactions for all active credentials (2 AM)
RecurringJob.AddOrUpdate<PortalSyncService>(
    "dha-daily-sync",
    svc => svc.RunDailyDhaSyncAsync(),
    Cron.Daily(2));

// Every 2 hours: parse any remittance XMLs not yet turned into claims (uses stored FileContentXml — no portal request)
RecurringJob.AddOrUpdate<RemittanceParserService>(
    "remittance-auto-parse",
    svc => svc.ParsePendingAsync(null),
    "0 */2 * * *");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // Create new tables added after initial EnsureCreated (no-op if already exist)
    db.Database.ExecuteSqlRaw(@"
        -- Performance indexes (safe to re-run — all IF NOT EXISTS)
        CREATE INDEX IF NOT EXISTS ""IX_PortalFetchLogs_FetchedAt""
            ON ""PortalFetchLogs""(""FetchedAt"" DESC);
        CREATE INDEX IF NOT EXISTS ""IX_PortalFetchLogs_FacilityId_Status""
            ON ""PortalFetchLogs""(""FacilityId"", ""Status"");
        CREATE INDEX IF NOT EXISTS ""IX_PortalFetchLogs_FacilityId_Operation""
            ON ""PortalFetchLogs""(""FacilityId"", ""Operation"");
        CREATE INDEX IF NOT EXISTS ""IX_PortalTransactions_FacilityId_FileDownloaded""
            ON ""PortalTransactions""(""FacilityId"", ""FileDownloaded"");
        CREATE INDEX IF NOT EXISTS ""IX_PortalTransactions_SyncedAt""
            ON ""PortalTransactions""(""SyncedAt"" DESC);
    ");
    // Add EmailTo column to ReportRequests if not present (SQLite safe migration)
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ReportRequests"" ADD COLUMN ""EmailTo"" TEXT NULL"); }
    catch { /* column already exists */ }

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""DhpoCodingSets"" (
            ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""Category"" TEXT NOT NULL,
            ""Code"" TEXT NOT NULL,
            ""Name"" TEXT NOT NULL,
            ""SubType"" TEXT NULL,
            ""ExtraJson"" TEXT NULL,
            ""ImportedAt"" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DhpoCodingSets_Category_Code""
            ON ""DhpoCodingSets""(""Category"", ""Code"");
    ");
    // SystemSettings and ReportSchedules tables (SQLite safe — no-op if already exist)
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""SystemSettings"" (
            ""Id""         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""Category""   TEXT NOT NULL,
            ""Key""        TEXT NOT NULL,
            ""Value""      TEXT NULL,
            ""UpdatedAt""  TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SystemSettings_Category_Key""
            ON ""SystemSettings""(""Category"", ""Key"");

        CREATE TABLE IF NOT EXISTS ""ReportSchedules"" (
            ""Id""              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""Name""            TEXT NOT NULL,
            ""ReportType""      TEXT NOT NULL,
            ""CronExpression""  TEXT NOT NULL DEFAULT '0 8 1 * *',
            ""Recipients""      TEXT NOT NULL,
            ""FileFormat""      TEXT NOT NULL DEFAULT 'Excel',
            ""FacilityIdsJson"" TEXT NULL,
            ""ParametersJson""  TEXT NULL,
            ""IsActive""        INTEGER NOT NULL DEFAULT 1,
            ""LastRunAt""       TEXT NULL,
            ""LastRunStatus""   TEXT NULL,
            ""CreatedAt""       TEXT NOT NULL DEFAULT (datetime('now'))
        );
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""RemittanceClaims"" (
            ""Id""                       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""RemittanceTransactionId""  INTEGER NOT NULL UNIQUE REFERENCES ""PortalTransactions""(""Id"") ON DELETE CASCADE,
            ""FacilityId""               INTEGER NOT NULL REFERENCES ""Facilities""(""Id"") ON DELETE CASCADE,
            ""ClaimId""                  TEXT NOT NULL,
            ""PayerClaimId""             TEXT NULL,
            ""PayerCode""                TEXT NULL,
            ""ClinicianLicense""         TEXT NULL,
            ""OriginalAmount""           REAL NOT NULL DEFAULT 0,
            ""PaidAmount""               REAL NOT NULL DEFAULT 0,
            ""DenialCodesJson""          TEXT NULL,
            ""Comments""                 TEXT NULL,
            ""ActivityCount""            INTEGER NOT NULL DEFAULT 0,
            ""SettlementDate""           TEXT NULL,
            ""PaymentReference""         TEXT NULL,
            ""ParsedAt""                 TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS ""IX_RemittanceClaims_FacilityId"" ON ""RemittanceClaims""(""FacilityId"");
        CREATE INDEX IF NOT EXISTS ""IX_RemittanceClaims_ClaimId""    ON ""RemittanceClaims""(""ClaimId"");

        CREATE TABLE IF NOT EXISTS ""ResubmissionTasks"" (
            ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""RemittanceClaimId""   INTEGER NOT NULL UNIQUE REFERENCES ""RemittanceClaims""(""Id"") ON DELETE CASCADE,
            ""AssignedToUserId""    TEXT NULL REFERENCES ""AspNetUsers""(""Id"") ON DELETE SET NULL,
            ""AssignedByUserId""    TEXT NULL REFERENCES ""AspNetUsers""(""Id"") ON DELETE SET NULL,
            ""AssignedAt""          TEXT NOT NULL DEFAULT (datetime('now')),
            ""DueDate""             TEXT NULL,
            ""Status""              TEXT NOT NULL DEFAULT 'Unassigned',
            ""Priority""            TEXT NOT NULL DEFAULT 'Normal',
            ""Notes""               TEXT NULL,
            ""ActionTaken""         TEXT NULL,
            ""StartedAt""           TEXT NULL,
            ""ResubmittedAt""       TEXT NULL,
            ""ClosedAt""            TEXT NULL,
            ""CreatedAt""           TEXT NOT NULL DEFAULT (datetime('now')),
            ""UpdatedAt""           TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE INDEX IF NOT EXISTS ""IX_ResubmissionTasks_Status""         ON ""ResubmissionTasks""(""Status"");
        CREATE INDEX IF NOT EXISTS ""IX_ResubmissionTasks_AssignedToUserId"" ON ""ResubmissionTasks""(""AssignedToUserId"");
    ");

    // Add ClaimCategory column if it doesn't exist yet
    try { db.Database.ExecuteSqlRaw(@"ALTER TABLE ""RemittanceClaims"" ADD COLUMN ""ClaimCategory"" TEXT NOT NULL DEFAULT 'Unknown'"); }
    catch { /* column already exists */ }

    await SeedData.InitializeAsync(services);
}

app.Run();

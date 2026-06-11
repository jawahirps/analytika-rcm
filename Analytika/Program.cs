using Analytika.Models;
using Analytika.Modules;
using Analytika.Services;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Data;

var builder = WebApplication.CreateBuilder(args);

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

// Respect PORT env variable (set by preview/hosting environment)
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

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
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "base-uri 'self'; " +
                "frame-ancestors 'none'; " +
                "form-action 'self'; " +
                "object-src 'none'; " +
                "img-src 'self' data: https:; " +
                "font-src 'self' https://fonts.gstatic.com https://cdnjs.cloudflare.com data:; " +
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
                "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://code.jquery.com https://cdnjs.cloudflare.com; " +
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
    app.UseHangfireDashboard("/hangfire");

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
    if (db.Database.IsNpgsql())
    {
        // Schema handled by Migrate() above; the raw SQL below is SQLite-specific
    }
    else
    {
    db.Database.EnsureCreated();
    var createIndexesOnStartup = app.Configuration.GetValue("StartupMaintenance:CreateIndexesOnStartup", false);
    if (createIndexesOnStartup)
    {
        db.Database.ExecuteSqlRaw(@"
            -- Performance indexes. Run only during planned maintenance on large local DBs.
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
    }
    // Add EmailTo column to ReportRequests if not present (SQLite safe migration)
    if (!ColumnExists(db, "ReportRequests", "EmailTo"))
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""ReportRequests"" ADD COLUMN ""EmailTo"" TEXT NULL");

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
            ""RemittanceTransactionId""  INTEGER NOT NULL REFERENCES ""PortalTransactions""(""Id"") ON DELETE CASCADE,
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
        CREATE TABLE IF NOT EXISTS ""XmlParsedRecords"" (
            ""Id""                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            ""PortalTransactionId"" INTEGER NOT NULL REFERENCES ""PortalTransactions""(""Id"") ON DELETE CASCADE,
            ""FacilityId""          INTEGER NOT NULL REFERENCES ""Facilities""(""Id"") ON DELETE CASCADE,
            ""RecordKind""          TEXT NOT NULL,
            ""ClaimId""             TEXT NOT NULL,
            ""FileName""            TEXT NULL,
            ""FileId""              TEXT NULL,
            ""TransactionDate""      TEXT NULL,
            ""SenderId""            TEXT NULL,
            ""ReceiverId""          TEXT NULL,
            ""ReceiverName""        TEXT NULL,
            ""PayerId""             TEXT NULL,
            ""PayerName""           TEXT NULL,
            ""PatientId""           TEXT NULL,
            ""MemberId""            TEXT NULL,
            ""TreatmentDate""       TEXT NULL,
            ""TreatmentDateEnd""    TEXT NULL,
            ""DateOfAdmission""     TEXT NULL,
            ""SubmissionDate""      TEXT NULL,
            ""EncounterType""       TEXT NULL,
            ""Clinician""           TEXT NULL,
            ""ServiceYear""         TEXT NULL,
            ""ServiceMonth""        TEXT NULL,
            ""NetAmount""           REAL NOT NULL DEFAULT 0,
            ""PaidAmount""          REAL NOT NULL DEFAULT 0,
            ""ActivityCount""       INTEGER NOT NULL DEFAULT 0,
            ""PaymentReference""    TEXT NULL,
            ""SettlementDate""      TEXT NULL,
            ""DenialCodesJson""     TEXT NULL,
            ""Comments""            TEXT NULL,
            ""IdPayer""             TEXT NULL,
            ""ResubmissionType""    TEXT NULL,
            ""PrincipalDiagnosis""  TEXT NULL,
            ""IsMatched""           INTEGER NOT NULL DEFAULT 0,
            ""ReadyForReport""      INTEGER NOT NULL DEFAULT 1,
            ""Notes""               TEXT NULL,
            ""ParsedAt""            TEXT NOT NULL,
            ""MatchedAt""           TEXT NULL
        );
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
    ");

    if (createIndexesOnStartup)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE INDEX IF NOT EXISTS ""IX_RemittanceClaims_FacilityId"" ON ""RemittanceClaims""(""FacilityId"");
            CREATE INDEX IF NOT EXISTS ""IX_RemittanceClaims_ClaimId""    ON ""RemittanceClaims""(""ClaimId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_PortalTransactionId"" ON ""XmlParsedRecords""(""PortalTransactionId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_Facility_Kind"" ON ""XmlParsedRecords""(""FacilityId"", ""RecordKind"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_ClaimId"" ON ""XmlParsedRecords""(""ClaimId"");
            CREATE INDEX IF NOT EXISTS ""IX_XmlParsedRecords_ReadyForReport"" ON ""XmlParsedRecords""(""ReadyForReport"");
            CREATE INDEX IF NOT EXISTS ""IX_ResubmissionTasks_Status"" ON ""ResubmissionTasks""(""Status"");
            CREATE INDEX IF NOT EXISTS ""IX_ResubmissionTasks_AssignedToUserId"" ON ""ResubmissionTasks""(""AssignedToUserId"");
        ");
    }

    // Add ClaimCategory column if it doesn't exist yet
    if (!ColumnExists(db, "RemittanceClaims", "ClaimCategory"))
        db.Database.ExecuteSqlRaw(@"ALTER TABLE ""RemittanceClaims"" ADD COLUMN ""ClaimCategory"" TEXT NOT NULL DEFAULT 'Unknown'");
    }

    if (!db.Database.IsNpgsql() && app.Configuration.GetValue("StartupMaintenance:SeedDataOnStartup", false))
        await SeedData.InitializeAsync(services);
}

app.Run();

static bool ColumnExists(AppDbContext db, string tableName, string columnName)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State != ConnectionState.Open;
    if (shouldClose) connection.Open();

    try
    {
        using var command = connection.CreateCommand();
        var safeTableName = tableName.Replace("\"", "\"\"");
        command.CommandText = $@"PRAGMA table_info(""{safeTableName}"")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
    finally
    {
        if (shouldClose) connection.Close();
    }
}

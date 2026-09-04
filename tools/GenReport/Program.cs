using Analytika.Models;
using Analytika.Modules;
using Analytika.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Boots the app's real DI graph and invokes IReportService.GenerateReportAsync so the
// output is byte-for-byte what the app produces (no reimplementation). Writes to the
// LIVE db + reports dir. Args: <facilityId> <yyyy-MM-dd from> <yyyy-MM-dd to> <criteria> <type...>
// Defaults below target New Lotus (10), June 2026, Claim Summary + Claim Activity.

string dataDir = Environment.GetEnvironmentVariable("DB_DIR") ?? @"J:\GhafAnalytika\bix-dev-data";
string dbPath  = Path.Combine(dataDir, "analytika.db");

int    facilityId = args.Length > 0 ? int.Parse(args[0]) : 10;
var    dateFrom   = args.Length > 1 ? DateTime.Parse(args[1]) : new DateTime(2026, 6, 1);
var    dateTo     = args.Length > 2 ? DateTime.Parse(args[2]) : new DateTime(2026, 6, 30);
string criteria   = args.Length > 3 ? args[3] : "SubmissionDate";
var    types      = args.Length > 4 ? args[4..] : new[] { "ClaimSummary", "ClaimActivity" };
string dateLabel  = $"{dateFrom:MMMyyyy}";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Environment.GetEnvironmentVariable("ANALYTIKA_CONTENT_ROOT") ?? @"J:\GhafAnalytika\Analytika-topnav\Analytika",
});
builder.Logging.ClearProviders();  // keep the console clean
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "dataprotection-keys")))
    .SetApplicationName("Analytika");
builder.Services.AddAnalytikaModules(builder.Configuration, dbPath, false, false, false);

var app = builder.Build();

using var scope = app.Services.CreateScope();
var sp = scope.ServiceProvider;
var db = sp.GetRequiredService<AppDbContext>();
db.Database.SetCommandTimeout(600);
var reports = sp.GetRequiredService<IReportService>();

var facName = facilityId > 0
    ? ((await db.Facilities.AsNoTracking().FirstOrDefaultAsync(f => f.Id == facilityId))?.Name ?? $"Facility {facilityId}")
    : "ALL BRANCHES";
Console.WriteLine($"Facility {facilityId} ({facName}) · {dateFrom:dd MMM yyyy} - {dateTo:dd MMM yyyy} · criteria={criteria}");

foreach (var type in types)
{
    var req = new ReportRequest
    {
        ReportType      = type,
        // facilityId <= 0 => ALL branches (no facility filter; ResolveFacilityIds -> empty)
        BranchId        = facilityId > 0 ? facilityId : null,
        FacilityIdsJson = facilityId > 0 ? $"[{facilityId}]" : null,
        DateFrom        = dateFrom,
        DateTo          = dateTo,
        SearchCriteria  = criteria,
        FileFormat      = "Excel",
        RequestedBy     = "codex@ghafbi.ae",
    };
    req.ReportId    = reports.GetNextReportId(req, dateLabel);
    req.Status      = "Pending";
    req.RequestedAt = DateTime.UtcNow;
    db.ReportRequests.Add(req);
    await db.SaveChangesAsync();

    Console.WriteLine($"\n[{type}] generating  id={req.Id}  reportId={req.ReportId} ...");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await reports.GenerateReportAsync(req.Id);
    var done = await db.ReportRequests.AsNoTracking().FirstAsync(r => r.Id == req.Id);
    Console.WriteLine($"[{type}] status={done.Status}  file={done.FilePath}  [{sw.Elapsed.TotalSeconds:F0}s]");
}
Console.WriteLine("\nDONE");

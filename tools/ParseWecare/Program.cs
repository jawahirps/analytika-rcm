using Analytika.Models;
using Analytika.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// Runs the application's REAL XmlParsingService against a single facility so the
// parsed records are byte-for-byte what the app would produce via Portal > XML
// Parsing. Points at the LIVE database explicitly (appsettings targets a dev
// worktree). Read side effect: inserts XmlParsedRecords/XmlParsedActivities for
// the target facility only (rebuild:false => skips already-parsed transactions).

int facilityId = args.Length > 0 && int.TryParse(args[0], out var f) ? f : 14; // 14 = wecare
const string ConnStr = "Data Source=J:\\GhafAnalytika\\Analytika\\analytika.db";

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite(ConnStr)
    .Options;

using var db = new AppDbContext(options);
// Keep one connection open so the busy_timeout pragma sticks for the whole run;
// gives us room to wait behind the live app's occasional writes instead of erroring.
db.Database.OpenConnection();
db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=60000");

var svc = new XmlParsingService(db, NullLogger<XmlParsingService>.Instance);

Console.WriteLine($"Parsing facility {facilityId} downloaded XML into the live DB ...");
var sw = System.Diagnostics.Stopwatch.StartNew();

var result = await svc.ParseDownloadedXmlAsync(
    facilityId: facilityId,
    rebuild: false,
    onProgress: p =>
    {
        if (p.Status is "start" or "done" || p.Done % 2000 == 0)
            Console.WriteLine($"  [{p.Status}] {p.Message}  ({p.Done:N0}/{p.Total:N0})  " +
                              $"records={p.Result.RecordsSaved:N0}  [{sw.Elapsed.TotalSeconds:F0}s]");
        return Task.CompletedTask;
    });

sw.Stop();
Console.WriteLine();
Console.WriteLine($"DONE in {sw.Elapsed.TotalSeconds:F0}s");
Console.WriteLine($"  files scanned  : {result.FilesScanned:N0}");
Console.WriteLine($"  files parsed   : {result.FilesParsed:N0}");
Console.WriteLine($"  files skipped  : {result.FilesSkipped:N0}");
Console.WriteLine($"  errors         : {result.Errors:N0}");
Console.WriteLine($"  records saved  : {result.RecordsSaved:N0}  (submission={result.SubmissionRows:N0}, remittance={result.RemittanceRows:N0})");
Console.WriteLine($"  matched refs   : {result.MatchedClaimRefs:N0}  (unmatched sub={result.UnmatchedSubmissions:N0}, rem={result.UnmatchedRemittances:N0})");

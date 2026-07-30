using Analytika.Models;
using Analytika.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

// Parses EVERY facility's downloaded-but-unparsed XML into the live DB using the app's
// REAL XmlParsingService — byte-for-byte identical to Portal > XML Parsing.
//
// Two-phase, because PortalTransactions physically holds ~40GB of inline XML blobs, so
// any streaming predicate scan crawls:
//   Phase 1  — a cheap id-only LEFT JOIN works out exactly which downloaded transactions
//              are not yet in XmlParsedRecords (no blob reads; ~1 min).
//   Phase 2  — ParsePendingByIdsAsync fetches strictly those rows by primary key and reads
//              each blob once. I/O is spent only on blobs that actually need parsing.
// rebuild is never used — this only clears the pending backlog and is safe to re-run.

const string ConnStr = "Data Source=J:\\GhafAnalytika\\Analytika\\analytika.db";

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite(ConnStr)
    .Options;

using var db = new AppDbContext(options);
db.Database.OpenConnection();
db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=60000");

// Optional CLI filter: pass facility IDs to restrict to those branches (default = all).
var only = args.Select(a => int.TryParse(a, out var v) ? v : -1).Where(v => v > 0).ToList();
var facFilter = only.Count > 0 ? $" AND p.FacilityId IN ({string.Join(",", only)})" : "";

Console.WriteLine($"ParseAll — two-phase, live DB{(only.Count > 0 ? $", facilities [{string.Join(",", only)}]" : ", ALL branches")}");
Console.WriteLine(new string('=', 72));

var overall = System.Diagnostics.Stopwatch.StartNew();

// ── Phase 1: find pending (downloaded, unparsed) transaction ids ─────────────────
Console.WriteLine("[phase 1] finding downloaded-but-unparsed transaction ids …");
var sw1 = System.Diagnostics.Stopwatch.StartNew();
var ids = new List<int>();
var conn = (SqliteConnection)db.Database.GetDbConnection();
using (var cmd = conn.CreateCommand())
{
    // NOT EXISTS, not LEFT JOIN on a DISTINCT subquery: the latter materialises every
    // parsed transaction id (1.3M+) into a temp b-tree before joining, which ran the
    // process out of memory ("Out of memory." mid phase 1). NOT EXISTS is a per-row
    // index seek against IX_XmlParsedRecords_PortalTransactionId — constant memory.
    // FileContentXml IS NOT NULL is a header-only check; no blob is read here.
    // FOCUS: claim submissions + remittance advice only — Prior files never yield
    // parsed records, so scanning them was pure waste (the 'skip swamp').
    cmd.CommandText = $@"
        SELECT p.Id
        FROM PortalTransactions p
        WHERE p.Portal = 'DHA' AND p.FileDownloaded = 1 AND p.FileContentXml IS NOT NULL
              AND p.Type IN ('Claim','Remittance'){facFilter}
              -- Skip files already proven to contain nothing extractable, otherwise every
              -- run re-reads them (they write no parsed rows, so NOT EXISTS keeps matching).
              AND COALESCE(p.ParseYieldedNothing, 0) = 0
              AND NOT EXISTS (SELECT 1 FROM XmlParsedRecords x
                              WHERE x.PortalTransactionId = p.Id)
        ORDER BY p.Id;";
    cmd.CommandTimeout = 0;
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read()) ids.Add(rdr.GetInt32(0));
}
sw1.Stop();
Console.WriteLine($"[phase 1] {ids.Count:N0} pending transaction(s)  [{sw1.Elapsed.TotalSeconds:F0}s]");

if (ids.Count == 0)
{
    Console.WriteLine("Nothing to parse — all downloaded XML is already parsed.");
    return;
}

// ── Phase 2: parse those ids by primary key ──────────────────────────────────────
Console.WriteLine("[phase 2] parsing pending transactions (reading each blob once) …");
var sw2 = System.Diagnostics.Stopwatch.StartNew();
var svc = new XmlParsingService(db, NullLogger<XmlParsingService>.Instance);
var result = await svc.ParsePendingByIdsAsync(
    ids,
    onProgress: p =>
    {
        if (p.Status == "parsing" && p.Done % 3000 == 0)
            Console.WriteLine($"    … {p.Done:N0}/{p.Total:N0}  records={p.Result.RecordsSaved:N0} (sub={p.Result.SubmissionRows:N0}, rem={p.Result.RemittanceRows:N0})  skipped={p.Result.FilesSkipped:N0}  [{sw2.Elapsed.TotalSeconds:F0}s]");
        return Task.CompletedTask;
    });
sw2.Stop();
overall.Stop();

Console.WriteLine("\n" + new string('=', 72));
Console.WriteLine(
    $"DONE in {overall.Elapsed.TotalMinutes:F1} min  (phase1 {sw1.Elapsed.TotalSeconds:F0}s + phase2 {sw2.Elapsed.TotalMinutes:F1} min)");
Console.WriteLine(
    $"  pending        : {result.FilesScanned:N0}");
Console.WriteLine(
    $"  parsed / skipped / errors : {result.FilesParsed:N0} / {result.FilesSkipped:N0} / {result.Errors:N0}");
Console.WriteLine(
    $"  records saved  : {result.RecordsSaved:N0}  (submission={result.SubmissionRows:N0}, remittance={result.RemittanceRows:N0})");
Console.WriteLine(
    $"  matched refs   : {result.MatchedClaimRefs:N0}  (unmatched sub={result.UnmatchedSubmissions:N0}, rem={result.UnmatchedRemittances:N0})");

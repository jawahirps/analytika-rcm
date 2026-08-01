using Analytika.Models;
using Analytika.Modules;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Drains the pending DHA download queue against the LIVE database, headless.
//
// Why a console tool rather than the in-app paths:
//   * The SSE path (Portal > Portal Files) is held open by a browser tab, so it dies on
//     every app restart/deploy and on tab close - it lost the queue repeatedly.
//   * PendingDownloadService is a daily top-up: one batch of <=500, sequential, and it
//     predates the archive fallback / focus filter / dead-file marking.
// This carries the same logic as the hardened SSE path: claims+remittance only,
// bounded parallel fetches, DHA Archive fallback when the normal endpoint returns an
// empty payload, and FileUnavailable marking so dead files are never retried.
//
// Args: [facilityId ...]   (default: all facilities)

const string DataDir = @"J:\GhafAnalytika\Analytika";
var dbPath = Path.Combine(DataDir, "analytika.db");

var only = args.Select(a => int.TryParse(a, out var v) ? v : -1).Where(v => v > 0).ToList();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = @"J:\GhafAnalytika\bix-app",   // deployed appsettings (portal URLs, cert policy)
});
builder.Logging.ClearProviders();
// Same key ring as the app, otherwise stored portal passwords cannot be decrypted.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DataDir, "dataprotection-keys")))
    .SetApplicationName("Analytika");
builder.Services.AddAnalytikaModules(builder.Configuration, dbPath, false, false, false);

var app = builder.Build();
using var scope = app.Services.CreateScope();
var sp = scope.ServiceProvider;
var db = sp.GetRequiredService<AppDbContext>();
db.Database.SetCommandTimeout(600);
var dha = sp.GetRequiredService<IDhaPortalService>();
var protector = sp.GetRequiredService<ICredentialProtector>();

db.Database.OpenConnection();
db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=120000");

Console.WriteLine($"DownloadAll - live DB{(only.Count > 0 ? $", facilities [{string.Join(",", only)}]" : ", ALL facilities")}");
Console.WriteLine(new string('=', 72));

// ── Credentials, decrypted once ───────────────────────────────────────────────
var credCache = new Dictionary<int, (string user, string pwd)>();
foreach (var c in await db.PortalCredentials.AsNoTracking()
             .Where(c => c.IsActive && c.Portal == "DHA")
             .Select(c => new { c.FacilityId, c.Username, c.PasswordEncrypted }).ToListAsync())
{
    try { credCache[c.FacilityId] = (c.Username, protector.Unprotect(c.PasswordEncrypted)); }
    catch { Console.WriteLine($"  ! cannot decrypt credential for facility {c.FacilityId} - skipped"); }
}
Console.WriteLine($"credentials loaded for {credCache.Count} facility(ies)");

// ── Pending queue ─────────────────────────────────────────────────────────────
// Claims + remittance only: Prior Request/Authorization files are ~78% of the queue
// and feed no report. FileUnavailable excludes files both portal endpoints refused.
var sw = System.Diagnostics.Stopwatch.StartNew();
Console.WriteLine("finding pending files ...");
var pendingQuery = db.PortalTransactions.AsNoTracking()
    .Where(t => !t.FileDownloaded && t.FileId != null && t.Portal == "DHA"
                && (t.Type == "Claim" || t.Type == "Remittance")
                && !t.FileUnavailable);
if (only.Count > 0) pendingQuery = pendingQuery.Where(t => only.Contains(t.FacilityId));

var pending = await pendingQuery
    .OrderBy(t => t.FacilityId).ThenBy(t => t.Id)
    .Select(t => new { t.Id, t.FacilityId, t.FileId })
    .ToListAsync();
Console.WriteLine($"{pending.Count:N0} file(s) pending  [{sw.Elapsed.TotalSeconds:F0}s]");
if (pending.Count == 0) { Console.WriteLine("nothing to download."); return; }

// ── Download ──────────────────────────────────────────────────────────────────
const int parallel = 10;   // portal round-trip dominates; DB writes stay sequential
int done = 0, failed = 0, dead = 0, processed = 0;
sw.Restart();

foreach (var chunk in pending.Chunk(parallel))
{
    var fetches = chunk.Select(async tx =>
    {
        if (!credCache.TryGetValue(tx.FacilityId, out var cred))
            return (tx, xml: (string?)null, size: 0L, ok: false, gone: false);
        try
        {
            var (res, _, bytes, err) = await dha.DownloadTransactionFileAsync(cred.user, cred.pwd, tx.FileId!);
            if (err == null && bytes?.Length > 0)
            {
                var (contentXml, _) = DhaPortalService.ParseDownloadedFile(bytes);
                return (tx, xml: contentXml, size: (long)bytes.Length, ok: true, gone: false);
            }
            // Empty payload from the normal endpoint = the file has aged out of it.
            // The Archive endpoint still serves those, so try there before giving up.
            var (ares, _, abytes, aerr) = await dha.DownloadTransactionFileArchiveAsync(cred.user, cred.pwd, tx.FileId!);
            if (aerr == null && abytes?.Length > 0)
            {
                var (contentXml, _) = DhaPortalService.ParseDownloadedFile(abytes);
                return (tx, xml: contentXml, size: (long)abytes.Length, ok: true, gone: false);
            }
            var bothEmpty = err == null && aerr == null;   // answered, but with nothing
            return (tx, xml: (string?)null, size: 0L, ok: false, gone: bothEmpty);
        }
        catch { return (tx, xml: (string?)null, size: 0L, ok: false, gone: false); }
    }).ToList();

    foreach (var (tx, xml, size, ok, gone) in await Task.WhenAll(fetches))
    {
        processed++;
        if (ok)
        {
            var saved = false;
            for (var attempt = 0; attempt < 3 && !saved; attempt++)
            {
                try
                {
                    await db.PortalTransactions.Where(t => t.Id == tx.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.FileDownloaded, true)
                            .SetProperty(t => t.FileContentXml, xml)
                            .SetProperty(t => t.FileSizeBytes, (long?)size)
                            .SetProperty(t => t.FileDownloadedAt, DateTime.UtcNow));
                    saved = true;
                }
                catch when (attempt < 2) { await Task.Delay(TimeSpan.FromSeconds(10 * (attempt + 1))); }
                catch { }
            }
            if (saved) done++; else failed++;
        }
        else
        {
            failed++;
            if (gone)
            {
                // Both endpoints answered with nothing: the portal no longer holds it.
                try
                {
                    await db.PortalTransactions.Where(t => t.Id == tx.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.FileUnavailable, true));
                    dead++;
                }
                catch { }
            }
        }

        if (processed % 500 == 0)
            Console.WriteLine($"  {processed:N0}/{pending.Count:N0}  downloaded={done:N0}  failed={failed:N0}  marked-gone={dead:N0}  [{sw.Elapsed.TotalMinutes:F1} min]");
    }
}

sw.Stop();
Console.WriteLine();
Console.WriteLine(new string('=', 72));
Console.WriteLine($"DONE in {sw.Elapsed.TotalMinutes:F1} min  |  downloaded={done:N0}  failed={failed:N0}  marked-unavailable={dead:N0}  of {pending.Count:N0}");

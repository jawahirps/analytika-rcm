using Analytika.Models;
using Analytika.Modules;
using Analytika.Security;
using Analytika.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var options = BackfillOptions.Parse(args);
if (options.Help)
{
    BackfillOptions.PrintHelp();
    return 0;
}

var contentRoot = Path.GetFullPath(options.ContentRoot ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Analytika"));
var dbDir = Path.GetFullPath(options.DbDir ?? contentRoot);
var dbPath = Path.Combine(dbDir, "analytika.db");

var configuration = new ConfigurationBuilder()
    .SetBasePath(contentRoot)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss "));
services.AddSingleton<IConfiguration>(configuration);
services.AddAnalytikaModules(configuration, dbPath, false, false, false);
await using var provider = services.BuildServiceProvider();

if (!File.Exists(dbPath) && DatabaseConfig.GetProvider(configuration) != DatabaseConfig.Postgres)
{
    Console.Error.WriteLine($"Database not found: {dbPath}");
    return 2;
}

List<FacilityTarget> targets;
await using (var scope = provider.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var targetRows = await db.PortalCredentials.AsNoTracking()
        .Where(c => c.IsActive && c.Portal == "DHA")
        .Join(db.Facilities.AsNoTracking(), c => c.FacilityId, f => f.Id,
            (c, f) => new { c.FacilityId, f.Name, CredentialId = c.Id })
        .OrderBy(x => x.FacilityId)
        .ToListAsync();
    targets = targetRows
        .Select(x => new FacilityTarget(x.FacilityId, x.Name, x.CredentialId))
        .ToList();
}

if (options.FacilityIds.Count > 0)
    targets = targets.Where(x => options.FacilityIds.Contains(x.FacilityId)).ToList();
if (options.PartitionCount > 1)
    targets = targets.Where((_, index) => index % options.PartitionCount == options.PartitionIndex).ToList();

Console.WriteLine($"Database: {dbPath}");
Console.WriteLine($"Range: {options.From:yyyy-MM-dd} through {options.To:yyyy-MM-dd} (monthly chunks)");
Console.WriteLine($"Facilities: {targets.Count}; workers: {options.Workers}; partition: {options.PartitionIndex + 1}/{options.PartitionCount}");
foreach (var target in targets)
    Console.WriteLine($"  {target.FacilityId,5}  {target.Name}");

if (options.List || options.DryRun)
{
    var chunks = MonthChunks(options.From, options.To);
    Console.WriteLine(options.List ? "List complete; no portal requests or database writes were made." : $"Dry run: {targets.Count * chunks.Count} facility-month job(s); no portal requests or database writes were made.");
    return 0;
}

if (!options.Execute || options.Confirmation != "BACKFILL")
{
    Console.Error.WriteLine("Execution refused. Supply both --execute and --confirm-write BACKFILL after reviewing --dry-run.");
    return 3;
}

Directory.CreateDirectory(dbDir);
await using var runLock = AcquireRunLock(Path.Combine(dbDir, ".analytika-backfill.lock"));
var writeGate = new SemaphoreSlim(1, 1); // SQLite parse/match/upsert writes must never overlap.
var failures = 0;
var completed = 0;

await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = options.Workers }, async (target, ct) =>
{
    try
    {
        var facilityNew = 0;
        var facilityDownloaded = 0;
        foreach (var (from, to) in MonthChunks(options.From, options.To))
        {
            Console.WriteLine($"[{target.FacilityId}] searching {from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
            List<Analytika.Models.ViewModels.PortalFetchResultRow> rows;
            string login;
            string password;
            await using (var searchScope = provider.CreateAsyncScope())
            {
                var db = searchScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var credential = await db.PortalCredentials.AsNoTracking().SingleAsync(x => x.Id == target.CredentialId, ct);
                login = credential.Username;
                password = searchScope.ServiceProvider.GetRequiredService<ICredentialProtector>().Unprotect(credential.PasswordEncrypted);
                var sync = searchScope.ServiceProvider.GetRequiredService<PortalSyncService>();
                var dhpoFrom = DhaPortalService.FormatDhpoDate(from.ToString("yyyy-MM-dd"));
                var dhpoTo = DhaPortalService.FormatDhpoDate(to.ToString("yyyy-MM-dd"), endOfDay: true);
                if (options.Archive)
                {
                    var dha = searchScope.ServiceProvider.GetRequiredService<IDhaPortalService>();
                    var found = new List<Analytika.Models.ViewModels.PortalFetchResultRow>();
                    foreach (var txType in options.SubmissionsOnly ? new[] { 2 } : new[] { 2, 8 })
                    foreach (var status in new[] { 1, 2 })
                    foreach (var direction in options.SubmissionsOnly ? new[] { 1 } : new[] { 1, 2 })
                    {
                        var (_, part, error) = await dha.SearchTransactionsArchiveAsync(
                            login, password, direction, dhpoFrom, dhpoTo, status, txType);
                        if (!string.IsNullOrWhiteSpace(error))
                            Console.Error.WriteLine($"[{target.FacilityId}] archive search warning: {error}");
                        var typeName = DhaPortalService.TxTypeName(txType);
                        foreach (var row in part) row.Type = typeName;
                        found.AddRange(part);
                    }
                    rows = found;
                }
                else
                {
                    rows = await sync.SearchAllCombosAsync(login, password, dhpoFrom, dhpoTo, [2, 8]);
                }
                rows = PortalSyncService.DeduplicateRows(rows);
            }

            Console.WriteLine($"[{target.FacilityId}] found {rows.Count:N0}; waiting for serialized save");
            await writeGate.WaitAsync(ct);
            try
            {
                await using var writeScope = provider.CreateAsyncScope();
                var sync = writeScope.ServiceProvider.GetRequiredService<PortalSyncService>();
                var saved = await sync.UpsertDhaTransactionsWithDownloadAsync(rows, login, password,
                    target.FacilityId, "ExternalBackfill", from.ToString("yyyy-MM"), "DHA");
                facilityNew += saved.newCount;
                facilityDownloaded += saved.filesDownloaded;
                Console.WriteLine($"[{target.FacilityId}] {from:yyyy-MM}: saved {saved.newCount:N0}, downloaded {saved.filesDownloaded:N0} [SQLite serialized]");
            }
            finally { writeGate.Release(); }
        }

        Console.WriteLine($"[{target.FacilityId}] fetch complete ({facilityNew:N0} new, {facilityDownloaded:N0} downloaded); waiting to parse and match");
        await writeGate.WaitAsync(ct);
        try
        {
            await using var parseScope = provider.CreateAsyncScope();
            var parser = parseScope.ServiceProvider.GetRequiredService<XmlParsingService>();
            await parser.EnsureSchemaAsync(ct);
            var parsed = await parser.ParseDownloadedXmlAsync(target.FacilityId, false, null, ct);
            var matched = await parser.MatchParsedRecordsAsync(target.FacilityId, ct);
            Console.WriteLine($"[{target.FacilityId}] plugged in: parsed {parsed.RecordsSaved:N0}, matched {matched.MatchedClaimRefs:N0}");
        }
        finally { writeGate.Release(); }
        Interlocked.Increment(ref completed);
    }
    catch (Exception ex)
    {
        Interlocked.Increment(ref failures);
        Console.Error.WriteLine($"[{target.FacilityId}] FAILED: {ex.Message}");
    }
});

Console.WriteLine($"Backfill complete: {completed} facilities completed, {failures} failed.");
return failures == 0 ? 0 : 1;

static List<(DateTime From, DateTime To)> MonthChunks(DateTime from, DateTime to)
{
    var result = new List<(DateTime, DateTime)>();
    var cursor = from.Date;
    while (cursor <= to.Date)
    {
        var end = new DateTime(cursor.Year, cursor.Month, 1).AddMonths(1).AddDays(-1);
        if (end > to.Date) end = to.Date;
        result.Add((cursor, end));
        cursor = end.AddDays(1);
    }
    result.Reverse();
    return result;
}

static FileStream AcquireRunLock(string path)
{
    try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { throw new InvalidOperationException($"Another backfill runner holds {path}"); }
}

internal sealed record FacilityTarget(int FacilityId, string Name, int CredentialId);

internal sealed class BackfillOptions
{
    public bool Help { get; private set; }
    public bool List { get; private set; }
    public bool DryRun { get; private set; } = true;
    public bool Execute { get; private set; }
    public bool Archive { get; private set; }
    public bool SubmissionsOnly { get; private set; }
    public string? Confirmation { get; private set; }
    public string? DbDir { get; private set; }
    public string? ContentRoot { get; private set; }
    public DateTime From { get; private set; } = DateTime.Today.AddYears(-2);
    public DateTime To { get; private set; } = DateTime.Today;
    public int Workers { get; private set; } = 12;
    public int PartitionIndex { get; private set; }
    public int PartitionCount { get; private set; } = 1;
    public HashSet<int> FacilityIds { get; } = [];

    public static BackfillOptions Parse(string[] args)
    {
        var o = new BackfillOptions();
        string Value(ref int i) => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": o.Help = true; break;
                case "--list": o.List = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--execute": o.Execute = true; o.DryRun = false; break;
                case "--archive": o.Archive = true; break;
                case "--submissions-only": o.SubmissionsOnly = true; break;
                case "--confirm-write": o.Confirmation = Value(ref i); break;
                case "--db-dir": o.DbDir = Value(ref i); break;
                case "--content-root": o.ContentRoot = Value(ref i); break;
                case "--from": o.From = DateTime.Parse(Value(ref i)).Date; break;
                case "--to": o.To = DateTime.Parse(Value(ref i)).Date; break;
                case "--workers": o.Workers = Math.Clamp(int.Parse(Value(ref i)), 1, 32); break;
                case "--partition-index": o.PartitionIndex = int.Parse(Value(ref i)); break;
                case "--partition-count": o.PartitionCount = int.Parse(Value(ref i)); break;
                case "--facility": foreach (var id in Value(ref i).Split(',')) o.FacilityIds.Add(int.Parse(id)); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (o.From > o.To) throw new ArgumentException("--from must be on or before --to");
        if (o.PartitionCount < 1 || o.PartitionIndex < 0 || o.PartitionIndex >= o.PartitionCount)
            throw new ArgumentException("Partition index is zero-based and must be less than partition count.");
        return o;
    }

    public static void PrintHelp() => Console.WriteLine("""
Analytika two-year DHA fetch/parse/match runner

Safe modes (default is --dry-run):
  --list                         List active DHA facilities only
  --dry-run                      Print the plan; never contact portals or write data

Selection:
  --facility 1,2,3               Run only these facility IDs
  --partition-count N            Split the ordered facility list into N partitions
  --partition-index N            Select zero-based partition N
  --from yyyy-MM-dd              Default: today minus two years
  --to yyyy-MM-dd                Default: today
  --workers N                    Parallel facility searches, 1..32 (default 12)
  --archive                      Use DHPO Archive search for historical transactions
  --submissions-only             Search only sent claim files (historical match repair)
  --db-dir PATH                  Directory containing analytika.db

Execution (both switches required):
  --execute --confirm-write BACKFILL

SQLite saves, XML parsing, and matching are serialized. Portal searches remain bounded-parallel.
Stop the web app before execution or use a copied database, then atomically deploy the completed DB.
""");
}

using System.Collections.Concurrent;
using System.Text;
using Analytika.Models;
using Analytika.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// Shared service for downloading DHA transaction files and persisting them to the DB.
/// Used by both PortalController (on-demand) and the Hangfire daily cron job.
/// </summary>
public class PortalSyncService
{
    private readonly AppDbContext _db;
    private readonly IDhaPortalService _dha;
    private readonly ILogger<PortalSyncService> _logger;

    public PortalSyncService(AppDbContext db, IDhaPortalService dha, ILogger<PortalSyncService> logger)
    {
        _db = db;
        _dha = dha;
        _logger = logger;
    }

    // ── Daily cron entry point ─────────────────────────────────────

    public async Task RunDailyDhaSyncAsync()
    {
        _logger.LogInformation("[CronSync] Starting daily DHA sync");

        var credentials = await _db.PortalCredentials
            .AsNoTracking()
            .Where(c => c.Portal == "DHA" && c.IsActive)
            .ToListAsync();

        _logger.LogInformation("[CronSync] {Count} active DHA credential(s) found", credentials.Count);

        foreach (var cred in credentials)
        {
            try { await SyncFacilityAsync(cred); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CronSync] Unhandled error for facility {Id}", cred.FacilityId);
            }
        }

        _logger.LogInformation("[CronSync] Daily DHA sync complete");
    }

    private async Task SyncFacilityAsync(PortalCredential cred)
    {
        var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cred.PasswordEncrypted));
        var dateFrom = DateTime.Today.AddDays(-90);
        var dateTo = DateTime.Today;
        var chunks = GetDateChunks(dateFrom, dateTo, 90);
        int[] txTypes = [2, 8, 16, 32];
        int totalNew = 0, totalFiles = 0;

        foreach (var (start, end) in chunks)
        {
            var dhpoFrom = DhaPortalService.FormatDhpoDate(start.ToString("yyyy-MM-dd"));
            var dhpoTo = DhaPortalService.FormatDhpoDate(end.ToString("yyyy-MM-dd"), endOfDay: true);
            var period = start.ToString("yyyy-MM");

            var allRows = await SearchAllCombosAsync(cred.Username, pwd, dhpoFrom, dhpoTo, txTypes, statuses: [1]);
            var uniqueRows = DeduplicateRows(allRows);
            if (!uniqueRows.Any()) continue;

            var (n, _, files) = await UpsertDhaTransactionsWithDownloadAsync(
                uniqueRows, cred.Username, pwd, cred.FacilityId, "CronSync", period, "DHA");
            totalNew += n; totalFiles += files;
        }

        _logger.LogInformation("[CronSync] Facility {Id}: {New} new, {Files} files downloaded",
            cred.FacilityId, totalNew, totalFiles);

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal = "DHA",
            FacilityId = cred.FacilityId,
            Operation = "CronSync",
            FetchedBy = "system",
            RecordsFetched = totalNew,
            Status = "Success",
            ResponseSummary = $"Cron: {totalNew} new records, {totalFiles} files (last 90 days)"
        });
        await _db.SaveChangesAsync();
    }

    // ── Core download + upsert ─────────────────────────────────────
    // • Deduplicates input by FileId before touching the DB.
    // • One batch DB query to find existing records (avoids N queries).
    // • Parallel downloads (max 5 concurrent) — big throughput gain.
    // • Retries download for existing records where FileDownloaded=false.
    // • Saves in batches of 100 to balance memory vs round-trips.
    // • Progress callback fires every 10 records (reduces SSE chatter).

    public async Task<(int newCount, int dupCount, int filesDownloaded)> UpsertDhaTransactionsWithDownloadAsync(
        List<PortalFetchResultRow> rows,
        string login, string pwd,
        int facilityId, string operation, string period,
        string portal,
        bool skipDownload = false,
        Func<int, int, int, Task>? onProgress = null)
    {
        if (!rows.Any()) return (0, 0, 0);

        // 1. Deduplicate input — same FileId can appear across multiple search calls
        var uniqueRows = DeduplicateRows(rows);
        if (!uniqueRows.Any()) return (0, 0, 0);

        var ids = uniqueRows.Select(r => r.FileId).ToList();

        // 2. One query — get existing records (id + download status)
        var existing = await _db.PortalTransactions
            .Where(t => t.Portal == portal && t.FacilityId == facilityId && ids.Contains(t.TransactionId))
            .Select(t => new { t.TransactionId, t.FileDownloaded })
            .ToListAsync();

        var existingSet = existing.Select(e => e.TransactionId).ToHashSet();
        var needsRetrySet = existing.Where(e => !e.FileDownloaded).Select(e => e.TransactionId).ToHashSet();
        var newRows = uniqueRows.Where(r => !existingSet.Contains(r.FileId)).ToList();
        var retryRows = uniqueRows.Where(r => needsRetrySet.Contains(r.FileId)).ToList();
        int dupCount = existingSet.Count - needsRetrySet.Count;   // already-downloaded = true dup

        int newCount = 0, filesDownloaded = 0;

        // 3. Parallel download new records
        if (newRows.Any())
        {
            var downloaded = await DownloadParallelAsync(newRows, login, pwd, facilityId, skipDownload, maxConcurrency: 5);
            var now = DateTime.UtcNow;
            int progressBatch = 0;

            foreach (var (row, contentXml, sizeBytes, dlOk) in downloaded)
            {
                _db.PortalTransactions.Add(new PortalTransaction
                {
                    Portal = portal,
                    FacilityId = facilityId,
                    TransactionId = row.FileId,
                    FileId = row.FileId,
                    Type = row.Type,
                    Status = row.Status,
                    FileName = row.FileName,
                    FileDownloaded = dlOk,
                    FileContentXml = contentXml,
                    FileSizeBytes = sizeBytes,
                    FileDownloadedAt = dlOk ? now : null,
                    TransactionDate = row.Date,
                    Payer = row.Payer,
                    Amount = row.Amount,
                    RawXml = row.RawXml,
                    Operation = operation,
                    SyncPeriod = period,
                    SyncedAt = now
                });

                if (dlOk) filesDownloaded++;
                newCount++;
                progressBatch++;

                if (newCount % 100 == 0)
                {
                    await _db.SaveChangesAsync();
                    if (onProgress != null) await onProgress(newCount, dupCount, filesDownloaded);
                    progressBatch = 0;
                }
            }

            if (progressBatch > 0) await _db.SaveChangesAsync();
        }

        // 4. Retry download for existing records that previously failed
        if (!skipDownload && retryRows.Any())
        {
            var retried = await DownloadParallelAsync(retryRows, login, pwd, facilityId, false, maxConcurrency: 3);
            var retryNow = DateTime.UtcNow;

            foreach (var (row, contentXml, sizeBytes, dlOk) in retried.Where(r => r.Downloaded))
            {
                await _db.PortalTransactions
                    .Where(t => t.Portal == portal && t.FacilityId == facilityId && t.TransactionId == row.FileId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.FileDownloaded, true)
                        .SetProperty(t => t.FileContentXml, contentXml)
                        .SetProperty(t => t.FileSizeBytes, sizeBytes)
                        .SetProperty(t => t.FileDownloadedAt, retryNow));
                filesDownloaded++;
            }
        }

        if (onProgress != null) await onProgress(newCount, dupCount, filesDownloaded);
        return (newCount, dupCount, filesDownloaded);
    }

    // ── Parallel download helper ───────────────────────────────────

    private async Task<List<(PortalFetchResultRow Row, string? ContentXml, long? SizeBytes, bool Downloaded)>>
        DownloadParallelAsync(
            List<PortalFetchResultRow> rows,
            string login, string pwd, int facilityId,
            bool skipDownload, int maxConcurrency)
    {
        var results = new ConcurrentBag<(PortalFetchResultRow, string?, long?, bool)>();
        var sem = new SemaphoreSlim(maxConcurrency);

        await Task.WhenAll(rows.Select(async row =>
        {
            await sem.WaitAsync();
            try
            {
                if (!skipDownload)
                {
                    try
                    {
                        var (_, dlFileName, dlBytes, _) = await _dha.DownloadTransactionFileAsync(login, pwd, row.FileId);
                        if (dlBytes?.Length > 0)
                        {
                            var (contentXml, _) = DhaPortalService.ParseDownloadedFile(dlBytes);
                            results.Add((row, contentXml, dlBytes.Length, true));

                            // Save to disk (non-critical, fire and forget)
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var dir = Path.Combine("wwwroot", "portal-downloads", $"facility-{facilityId}");
                                    Directory.CreateDirectory(dir);
                                    var name = string.IsNullOrWhiteSpace(dlFileName) ? $"{row.FileId}.xml" : dlFileName;
                                    await File.WriteAllBytesAsync(Path.Combine(dir, name), dlBytes);
                                }
                                catch (Exception ex) { _logger.LogDebug(ex, "[PortalSync] Disk write failed for {FileId}", row.FileId); }
                            });
                            return;
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "[PortalSync] Download failed for {FileId}", row.FileId); }
                }
                results.Add((row, null, null, false));
            }
            finally { sem.Release(); }
        }));

        return results.ToList();
    }

    // ── Parallel search helper ─────────────────────────────────────
    // Runs all type × status × direction combinations in parallel (max 4 at a time).

    public async Task<List<PortalFetchResultRow>> SearchAllCombosAsync(
        string login, string pwd,
        string? dhpoFrom, string? dhpoTo,
        int[] txTypes, int[]? statuses = null)
    {
        statuses ??= [1, 2];
        var combos = (from t in txTypes from s in statuses select (t, s)).ToList();
        var sem = new SemaphoreSlim(4);
        var bag = new ConcurrentBag<PortalFetchResultRow>();

        await Task.WhenAll(combos.Select(async combo =>
        {
            await sem.WaitAsync();
            try
            {
                var (_, sent, _) = await _dha.SearchTransactionsAsync(login, pwd, 1, dhpoFrom, dhpoTo, combo.s, combo.t);
                var (_, recv, _) = await _dha.SearchTransactionsAsync(login, pwd, 2, dhpoFrom, dhpoTo, combo.s, combo.t);
                foreach (var r in sent) bag.Add(r);
                foreach (var r in recv) bag.Add(r);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[PortalSync] SearchAllCombos failed for combo ({TxType}, {Status})", combo.t, combo.s); }
            finally { sem.Release(); }
        }));

        return bag.ToList();
    }

    // ── Utilities ─────────────────────────────────────────────────

    public static List<PortalFetchResultRow> DeduplicateRows(List<PortalFetchResultRow> rows) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.FileId) && r.FileId != "-")
            .GroupBy(r => r.FileId)
            .Select(g => g.First())
            .ToList();

    /// <summary>Splits [from, to] into chunks of at most maxDays each.</summary>
    public static List<(DateTime Start, DateTime End)> GetDateChunks(DateTime from, DateTime to, int maxDays)
    {
        var chunks = new List<(DateTime, DateTime)>();
        var cursor = from;
        while (cursor <= to)
        {
            var end = cursor.AddDays(maxDays - 1);
            if (end > to) end = to;
            chunks.Add((cursor, end));
            cursor = end.AddDays(1);
        }
        return chunks;
    }
}

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
    // Scheduled via Hangfire — syncs last 90 days for all active DHA credentials.
    // 90-day window stays safely under the portal's 100-day hard limit (error -5).

    public async Task RunDailyDhaSyncAsync()
    {
        _logger.LogInformation("[CronSync] Starting daily DHA sync");

        var credentials = await _db.PortalCredentials
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

        // Last 90 days in one chunk (< 100-day portal limit)
        var dateFrom = DateTime.Today.AddDays(-90);
        var dateTo   = DateTime.Today;
        var chunks   = GetDateChunks(dateFrom, dateTo, 90);

        int[] txTypes = [2, 8, 16, 32];
        int totalNew = 0, totalFiles = 0;

        foreach (var (start, end) in chunks)
        {
            var dhpoFrom = DhaPortalService.FormatDhpoDate(start.ToString("yyyy-MM-dd"));
            var dhpoTo   = DhaPortalService.FormatDhpoDate(end.ToString("yyyy-MM-dd"), endOfDay: true);
            var period   = start.ToString("yyyy-MM");

            var allRows = new List<PortalFetchResultRow>();
            foreach (var txType in txTypes)
            {
                var (_, rowsSent, _) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 1, dhpoFrom, dhpoTo, 1, txType);
                var (_, rowsRecv, _) = await _dha.SearchTransactionsAsync(cred.Username, pwd, 2, dhpoFrom, dhpoTo, 1, txType);
                allRows.AddRange(rowsSent);
                allRows.AddRange(rowsRecv);
            }

            var uniqueRows = allRows
                .GroupBy(r => r.FileId)
                .Select(g => g.First())
                .ToList();

            if (!uniqueRows.Any()) continue;

            var (n, dups, files) = await UpsertDhaTransactionsWithDownloadAsync(
                uniqueRows, cred.Username, pwd, cred.FacilityId, "CronSync", period, "DHA");

            totalNew   += n;
            totalFiles += files;
        }

        _logger.LogInformation("[CronSync] Facility {Id}: {New} new, {Files} files downloaded",
            cred.FacilityId, totalNew, totalFiles);

        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Portal          = "DHA",
            FacilityId      = cred.FacilityId,
            Operation       = "CronSync",
            FetchedBy       = "system",
            RecordsFetched  = totalNew,
            Status          = "Success",
            ResponseSummary = $"Cron: {totalNew} new records, {totalFiles} files (last 90 days)"
        });
        await _db.SaveChangesAsync();
    }

    // ── Core download + upsert (shared with controller) ───────────
    // Iterates rows, downloads each file, persists to PortalTransactions.

    public async Task<(int newCount, int dupCount, int filesDownloaded)> UpsertDhaTransactionsWithDownloadAsync(
        List<PortalFetchResultRow> rows,
        string login, string pwd,
        int facilityId, string operation, string period,
        string portal,                                   // "DHA" or "RHA" — must be explicit
        bool skipDownload = false,
        Func<int, int, int, Task>? onProgress = null)   // (saved, duplicates, filesDownloaded)
    {
        int newCount = 0, dupCount = 0, filesDownloaded = 0;

        if (!rows.Any()) return (0, 0, 0);

        var ids = rows
            .Select(r => r.FileId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != "-")
            .Distinct().ToList();

        var existingIds = await _db.PortalTransactions
            .Where(t => t.Portal == portal && t.FacilityId == facilityId && ids.Contains(t.TransactionId))
            .Select(t => t.TransactionId)
            .ToHashSetAsync();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.FileId) || row.FileId == "-") continue;
            if (existingIds.Contains(row.FileId))
            {
                dupCount++;
                if (onProgress != null) await onProgress(newCount, dupCount, filesDownloaded);
                continue;
            }

            string? fileContentXml    = null;
            long?   fileSizeBytes     = null;
            DateTime? fileDownloadedAt = null;
            bool fileDownloaded       = false;

            if (!skipDownload && !string.IsNullOrWhiteSpace(row.FileId) && !string.IsNullOrEmpty(login))
            {
                try
                {
                    var (_, dlFileName, dlBytes, _) = await _dha.DownloadTransactionFileAsync(login, pwd, row.FileId);
                    if (dlBytes != null && dlBytes.Length > 0)
                    {
                        var (contentXml, _) = DhaPortalService.ParseDownloadedFile(dlBytes);
                        fileContentXml    = contentXml;
                        fileSizeBytes     = dlBytes.Length;
                        fileDownloadedAt  = DateTime.UtcNow;
                        fileDownloaded    = true;
                        filesDownloaded++;

                        try
                        {
                            var dir      = Path.Combine("wwwroot", "portal-downloads", $"facility-{facilityId}");
                            Directory.CreateDirectory(dir);
                            var safeName = string.IsNullOrWhiteSpace(dlFileName) ? $"{row.FileId}.xml" : dlFileName;
                            await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, safeName), dlBytes);
                        }
                        catch { /* disk write is non-critical */ }
                    }
                }
                catch { /* download failure is non-critical — still save the record */ }
            }

            _db.PortalTransactions.Add(new PortalTransaction
            {
                Portal           = portal,
                FacilityId       = facilityId,
                TransactionId    = row.FileId,   // FileID is the unique key for the DB record
                Type             = row.Type,
                Status           = row.Status,
                FileId           = row.FileId,
                FileName         = row.FileName,
                FileDownloaded   = fileDownloaded,
                FileContentXml   = fileContentXml,
                FileSizeBytes    = fileSizeBytes,
                FileDownloadedAt = fileDownloadedAt,
                TransactionDate  = row.Date,
                Payer            = row.Payer,
                Amount           = row.Amount,
                RawXml           = row.RawXml,
                Operation        = operation,
                SyncPeriod       = period,
                SyncedAt         = DateTime.UtcNow
            });

            existingIds.Add(row.FileId);
            newCount++;
            if (onProgress != null) await onProgress(newCount, dupCount, filesDownloaded);

            if (newCount % 50 == 0)
                await _db.SaveChangesAsync();
        }

        if (newCount % 50 != 0)
            await _db.SaveChangesAsync();

        return (newCount, dupCount, filesDownloaded);
    }

    // ── Utility ────────────────────────────────────────────────────

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

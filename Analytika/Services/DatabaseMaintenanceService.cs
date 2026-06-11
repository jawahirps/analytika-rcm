using Analytika.Models;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Services;

/// <summary>
/// Hangfire-driven maintenance: nightly SQLite backups with rotation, and
/// data-retention cleanup (old XML blobs, downloaded files, fetch logs).
/// </summary>
public class DatabaseMaintenanceService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DatabaseMaintenanceService> _logger;

    public DatabaseMaintenanceService(AppDbContext db, IConfiguration config, IWebHostEnvironment env, ILogger<DatabaseMaintenanceService> logger)
    {
        _db = db;
        _config = config;
        _env = env;
        _logger = logger;
    }

    private string GetDataDir() =>
        Environment.GetEnvironmentVariable("DB_DIR") ?? _env.ContentRootPath;

    // ── Nightly backup with rotation ───────────────────────────────

    public async Task BackupDatabaseAsync()
    {
        var dataDir = GetDataDir();
        var dbPath = Path.Combine(dataDir, "analytika.db");
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"Database file not found at {dbPath}");

        var backupDir = _config["Backup:Directory"] ?? Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backupDir);

        var started = DateTime.UtcNow;

        // Merge WAL into the main DB file so the copy is complete (same as Admin → Export Database)
        await _db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL)");

        var backupName = $"analytika_{DateTime.UtcNow:yyyyMMdd_HHmm}.db";
        var backupPath = Path.Combine(backupDir, backupName);
        var tmpPath = backupPath + ".tmp";
        File.Copy(dbPath, tmpPath, overwrite: true);
        File.Move(tmpPath, backupPath, overwrite: true);

        var retentionCount = _config.GetValue("Backup:RetentionCount", 14);
        var old = Directory.GetFiles(backupDir, "analytika_*.db")
            .OrderByDescending(f => f)
            .Skip(retentionCount)
            .ToList();
        foreach (var f in old)
        {
            try { File.Delete(f); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Backup] Failed to delete old backup {File}", f); }
        }

        var size = new FileInfo(backupPath).Length;
        _logger.LogInformation("[Backup] {Name} written ({Size:N0} bytes, {Duration:N1}s); {Deleted} old backup(s) rotated out",
            backupName, size, (DateTime.UtcNow - started).TotalSeconds, old.Count);
    }

    // ── Data retention ─────────────────────────────────────────────

    public async Task RunRetentionAsync()
    {
        if (!_config.GetValue("Retention:Enabled", true))
        {
            _logger.LogInformation("[Retention] Disabled via Retention:Enabled=false — skipped");
            return;
        }

        var months = _config.GetValue("Retention:XmlContentMonths", 12);
        var cutoff = DateTime.UtcNow.AddMonths(-months);

        // 1. Null out FileContentXml for old transactions that have been fully consumed.
        //    Conditions guard the two downstream consumers:
        //    - XmlParsingService incremental mode skips rows that already have XmlParsedRecords
        //    - RemittanceParserService only targets remittances without a RemittanceClaims row
        //    Side effects (accepted): rebuild/re-parse and the raw-XML download return nothing
        //    for purged rows; FileDownloaded stays true so they are not re-downloaded.
        try
        {
            var purged = await _db.PortalTransactions
                .Where(t => t.SyncedAt < cutoff
                         && t.FileContentXml != null
                         && _db.XmlParsedRecords.Any(r => r.PortalTransactionId == t.Id)
                         && (!t.Type.Contains("Remittance")
                             || _db.RemittanceClaims.Any(rc => rc.RemittanceTransactionId == t.Id)))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.FileContentXml, (string?)null));
            _logger.LogInformation("[Retention] Purged XML content from {Count} transaction(s) older than {Months} months", purged, months);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retention] XML blob purge failed");
        }

        // 2. Delete old downloaded files from disk
        try
        {
            var downloadsDir = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "portal-downloads");
            int deleted = 0;
            if (Directory.Exists(downloadsDir))
            {
                foreach (var file in Directory.EnumerateFiles(downloadsDir, "*", SearchOption.AllDirectories))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        try { File.Delete(file); deleted++; }
                        catch (Exception ex) { _logger.LogWarning(ex, "[Retention] Failed to delete {File}", file); }
                    }
                }
                foreach (var dir in Directory.EnumerateDirectories(downloadsDir, "facility-*"))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        try { Directory.Delete(dir); }
                        catch (Exception ex) { _logger.LogWarning(ex, "[Retention] Failed to remove empty dir {Dir}", dir); }
                    }
                }
            }
            _logger.LogInformation("[Retention] Deleted {Count} downloaded file(s) older than {Months} months", deleted, months);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retention] Disk cleanup failed");
        }

        // 3. Trim old fetch logs
        try
        {
            var logDays = _config.GetValue("Retention:FetchLogDays", 90);
            var logCutoff = DateTime.UtcNow.AddDays(-logDays);
            var logsDeleted = await _db.PortalFetchLogs
                .Where(l => l.FetchedAt < logCutoff)
                .ExecuteDeleteAsync();
            _logger.LogInformation("[Retention] Deleted {Count} fetch log(s) older than {Days} days", logsDeleted, logDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retention] Fetch log cleanup failed");
        }
    }
}

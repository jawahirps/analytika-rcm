using Analytika.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace Analytika.Services;

/// <summary>
/// Long-running hosted service that downloads XML files for all PortalTransactions
/// where FileDownloaded = false.  Survives browser close and server restart — the
/// DB itself is the persistent queue.
///
/// Scheduling:
///   • Runs one small batch daily at the configured least-used local time.
///   • Can be woken immediately via PendingDownloadState.Trigger() (called by the
///     "Run Now" UI button through the TriggerPendingDownload controller action).
/// </summary>
public class PendingDownloadService : BackgroundService
{
    private static readonly TimeSpan DefaultScheduledLocalTime = new(2, 30, 0);

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<PendingDownloadService> _logger;
    private readonly Analytika.Security.ICredentialProtector _credentials;

    public PendingDownloadService(IServiceProvider services, IConfiguration config, ILogger<PendingDownloadService> logger, Analytika.Security.ICredentialProtector credentials)
    {
        _services = services;
        _config = config;
        _logger = logger;
        _credentials = credentials;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduledRunEnabled = _config.GetValue("BackgroundJobs:PendingDownloads:ScheduledRunEnabled", true);
        var scheduledLocalTime = GetScheduledLocalTime();
        var batchSize = GetBatchSize();
        _logger.LogInformation("[PendingDownload] Service started — scheduled run {mode} at {time}, batch size {batchSize}",
            scheduledRunEnabled ? "enabled" : "disabled",
            scheduledLocalTime.ToString(@"hh\:mm"),
            batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reason = await WaitForNextRunAsync(scheduledRunEnabled, scheduledLocalTime, stoppingToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("[PendingDownload] Starting {reason} batch", reason);
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PendingDownload] Unhandled error during run");
            }
        }

        _logger.LogInformation("[PendingDownload] Service stopping");
    }

    // ── Core download logic ────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        var batchSize = GetBatchSize();
        var autoParse = _config.GetValue("BackgroundJobs:PendingDownloads:AutoParseRemittance", false);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dha = scope.ServiceProvider.GetRequiredService<IDhaPortalService>();
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        var parser = scope.ServiceProvider.GetRequiredService<RemittanceParserService>();

        var pending = await db.PortalTransactions
            .Where(t => !t.FileDownloaded && t.FileId != null && t.Portal == "DHA")
            .Select(t => new { t.Id, t.FacilityId, t.FileId, t.Type, t.TransactionId })
            .OrderBy(t => t.FacilityId)
            .ThenBy(t => t.Id)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct);

        if (!pending.Any())
        {
            // Even with no new downloads, parse anything not yet converted to claims
            if (autoParse)
                await AutoParseAsync(parser, 0);
            return;
        }

        _logger.LogInformation("[PendingDownload] Starting run — processing up to {count} pending files", pending.Count);
        PendingDownloadState.Start(pending.Count);

        // Batch-load all DHA credentials upfront instead of one-by-one in loop
        var allCredentials = await db.PortalCredentials
            .Where(c => c.IsActive && c.Portal == "DHA")
            .Select(c => new { c.FacilityId, c.Username, c.PasswordEncrypted })
            .AsNoTracking()
            .ToListAsync(ct);

        var credCache = new Dictionary<int, (string username, string pwd)>();
        foreach (var cr in allCredentials)
        {
            try
            {
                var pwd = _credentials.Unprotect(cr.PasswordEncrypted);
                credCache[cr.FacilityId] = (cr.Username, pwd);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PendingDownload] Failed to decode password for facility {fid}", cr.FacilityId);
            }
        }

        int done = 0, failed = 0, doneRemittance = 0;

        foreach (var tx in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Resolve credentials from pre-loaded cache
            if (!credCache.TryGetValue(tx.FacilityId, out var cred))
            {
                _logger.LogWarning("[PendingDownload] No DHA credentials for facility {fid} — skipping", tx.FacilityId);
                failed++;
                continue;
            }

            // Download file
            try
            {
                var (_, _, dlBytes, dlErr) = await dha.DownloadTransactionFileAsync(
                    cred.username, cred.pwd, tx.FileId!);

                if (dlErr == null && dlBytes?.Length > 0)
                {
                    var (contentXml, _) = DhaPortalService.ParseDownloadedFile(dlBytes);
                    await db.PortalTransactions
                        .Where(t => t.Id == tx.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.FileDownloaded, true)
                            .SetProperty(t => t.FileContentXml, contentXml)
                            .SetProperty(t => t.FileSizeBytes, (long?)dlBytes.Length)
                            .SetProperty(t => t.FileDownloadedAt, DateTime.UtcNow), ct);
                    done++;
                    if (tx.Type == "Remittance")
                        doneRemittance++;
                }
                else
                {
                    _logger.LogDebug("[PendingDownload] Skipped {tid}: {err}", tx.TransactionId, dlErr ?? "no bytes");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PendingDownload] Error downloading {tid}", tx.TransactionId);
                failed++;
            }

            // Report progress every 10 records
            if ((done + failed) % 10 == 0)
                PendingDownloadState.Update(done, failed, $"Facility {tx.FacilityId}");
        }

        cache.Remove("statusbar_static");
        PendingDownloadState.Finish(done, failed);
        _logger.LogInformation("[PendingDownload] Run complete — {done} downloaded ({rem} remittance), {failed} failed / {total} total",
            done, doneRemittance, failed, pending.Count);

        // Auto-parse: convert newly downloaded remittance XMLs into claims (reads FileContentXml already in DB)
        if (autoParse)
            await AutoParseAsync(parser, doneRemittance);
    }

    private async Task<string> WaitForNextRunAsync(bool scheduledRunEnabled, TimeSpan scheduledLocalTime, CancellationToken stoppingToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var triggerTask = PendingDownloadState.TriggerReader.ReadAsync(waitCts.Token).AsTask();
        var delayTask = scheduledRunEnabled
            ? Task.Delay(GetDelayUntilNextRun(scheduledLocalTime, DateTime.Now), waitCts.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token);

        try
        {
            var completed = await Task.WhenAny(triggerTask, delayTask).ConfigureAwait(false);
            waitCts.Cancel();

            if (completed == triggerTask)
            {
                await triggerTask.ConfigureAwait(false);
                return "manual";
            }

            await delayTask.ConfigureAwait(false);
            return "scheduled";
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return "manual";
        }
    }

    private int GetBatchSize()
    {
        return Math.Clamp(_config.GetValue("BackgroundJobs:PendingDownloads:BatchSize", 25), 1, 500);
    }

    private TimeSpan GetScheduledLocalTime()
    {
        var configured = _config.GetValue<string>("BackgroundJobs:PendingDownloads:ScheduledLocalTime");
        return TimeSpan.TryParse(configured, out var parsed) ? parsed : DefaultScheduledLocalTime;
    }

    private static TimeSpan GetDelayUntilNextRun(TimeSpan scheduledLocalTime, DateTime now)
    {
        var nextRun = now.Date.Add(scheduledLocalTime);
        if (nextRun <= now)
            nextRun = nextRun.AddDays(1);

        return nextRun - now;
    }

    private async Task AutoParseAsync(RemittanceParserService parser, int newRemittanceCount)
    {
        try
        {
            var (parsed, skipped, errors) = await parser.ParsePendingAsync();
            if (parsed > 0 || newRemittanceCount > 0)
                _logger.LogInformation("[PendingDownload] Auto-parse: {parsed} new claims created, {skipped} skipped, {errors} errors",
                    parsed, skipped, errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PendingDownload] Auto-parse failed");
        }
    }
}

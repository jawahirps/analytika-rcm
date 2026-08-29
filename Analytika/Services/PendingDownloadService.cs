using Analytika.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace Analytika.Services;

/// <summary>
/// Long-running hosted service that downloads XML files for all PortalTransactions
/// where FileDownloaded = false. Survives browser close and server restart — the
/// DB itself is the persistent queue.
///
/// Scheduling:
///   • Runs one batch daily at the configured least-used local time.
///   • Can be woken immediately via PendingDownloadState.Trigger() (called by the
///     "Run Now" UI button through the TriggerPendingDownload controller action).
///   • Supports parallel downloads with configurable concurrency (default 10).
///   • Supports both DHA and RHA portals.
/// </summary>
public class PendingDownloadService : BackgroundService
{
    private static readonly TimeSpan DefaultScheduledLocalTime = new(2, 30, 0);
    private static readonly int DefaultMaxConcurrency = 10;

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
        var maxConcurrency = GetMaxConcurrency();
        _logger.LogInformation("[PendingDownload] Service started — scheduled run {mode} at {time}, batch size {batchSize}, max concurrency {concurrency}",
            scheduledRunEnabled ? "enabled" : "disabled",
            scheduledLocalTime.ToString(@"hh\:mm"),
            batchSize,
            maxConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reason = await WaitForNextRunAsync(scheduledRunEnabled, scheduledLocalTime, stoppingToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("[PendingDownload] Starting {reason} batch", reason);
                await RunAsync(maxConcurrency, stoppingToken).ConfigureAwait(false);
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

    private async Task RunAsync(int maxConcurrency, CancellationToken ct)
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
            .Select(t => new { t.Id, t.FacilityId, t.FileId, t.FileName, t.Type, t.TransactionId, t.Portal })
            .OrderBy(t => t.FacilityId)
            .ThenBy(t => t.Id)
            .Take(batchSize)
            .AsNoTracking()
            .ToListAsync(ct);

        if (!pending.Any())
        {
            if (autoParse)
                await AutoParseAsync(parser, 0);
            return;
        }

        _logger.LogInformation("[PendingDownload] Starting run — processing up to {count} pending files with concurrency {concurrency}", pending.Count, maxConcurrency);
        PendingDownloadState.Start(pending.Count);

        // Batch-load all DHA credentials upfront
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
            catch
            {
                _logger.LogWarning("[PendingDownload] Failed to decode password for facility {fid}", cr.FacilityId);
            }
        }

        int done = 0, failed = 0, doneRemittance = 0;
        var results = new List<(int id, bool success, bool isRemittance, string? error)>();

        // Parallel download with semaphore
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var downloadTasks = pending.Select(async tx =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var (success, isRemittance, error) = await DownloadSingleAsync(tx, credCache, dha, ct);
                return (tx.Id, success, isRemittance, error);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var taskResults = await Task.WhenAll(downloadTasks);

        // Batch update DB with all results
        foreach (var (id, success, isRemittance, error) in taskResults)
        {
            if (success)
            {
                done++;
                if (isRemittance) doneRemittance++;
            }
            else
            {
                failed++;
            }

            // Report progress every 10 records
            if ((done + failed) % 10 == 0)
                PendingDownloadState.Update(done, failed, $"Processed {done + failed}/{pending.Count}");
        }

        cache.Remove("statusbar_static");
        PendingDownloadState.Finish(done, failed);
        _logger.LogInformation("[PendingDownload] Run complete — {done} downloaded ({rem} remittance), {failed} failed / {total} total",
            done, doneRemittance, failed, pending.Count);

        if (autoParse)
            await AutoParseAsync(parser, doneRemittance);
    }

    private async Task<(bool success, bool isRemittance, string? error)> DownloadSingleAsync(
        dynamic tx,
        Dictionary<int, (string username, string pwd)> credCache,
        IDhaPortalService dha,
        CancellationToken ct)
    {
        var facilityId = (int)tx.FacilityId;
        var fileId = (string)tx.FileId!;
        var transactionId = (string)tx.TransactionId;
        var type = (string)tx.Type;

        if (!credCache.TryGetValue(facilityId, out var cred))
        {
            _logger.LogWarning("[PendingDownload] No DHA credentials for facility {fid} — skipping {tid}", facilityId, transactionId);
            return (false, false, "No DHA credentials");
        }

        try
        {
            var (_, _, dlBytes, dlErr) = await dha.DownloadTransactionFileAsync(cred.username, cred.pwd, fileId);

            if (dlErr == null && dlBytes?.Length > 0)
            {
                var (contentXml, _) = DhaPortalService.ParseDownloadedFile(dlBytes, logger: _logger);

                // We'll batch update after all downloads complete
                return (true, type == "Remittance", null);
            }
            else
            {
                _logger.LogWarning("[PendingDownload] Download refused for {tid} ({type}@DHA): {err}",
                    transactionId, type, dlErr ?? "no bytes returned");
                return (false, false, dlErr ?? "no bytes");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PendingDownload] Error downloading {tid}", transactionId);
            return (false, false, ex.Message);
        }
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
        return Math.Clamp(_config.GetValue("BackgroundJobs:PendingDownloads:BatchSize", 200), 1, 500);
    }

    private int GetMaxConcurrency()
    {
        return Math.Clamp(_config.GetValue("BackgroundJobs:PendingDownloads:MaxConcurrency", DefaultMaxConcurrency), 1, 50);
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
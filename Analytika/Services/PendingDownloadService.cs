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
///   • Runs once ~30 s after app boot (catches any files left pending from the last run).
///   • Repeats every 30 minutes automatically.
///   • Can be woken immediately via PendingDownloadState.Trigger() (called by the
///     "Run Now" UI button through the TriggerPendingDownload controller action).
/// </summary>
public class PendingDownloadService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<PendingDownloadService> _logger;

    public PendingDownloadService(IServiceProvider services, ILogger<PendingDownloadService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[PendingDownload] Service started — first run in {delay}s", StartupDelay.TotalSeconds);

        // Brief startup delay so the app/EF fully initialises before we touch the DB
        await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PendingDownload] Unhandled error during run");
            }

            // Sleep until the next scheduled interval or an explicit trigger, whichever comes first
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                await Task.WhenAny(
                    Task.Delay(PollingInterval, cts.Token),
                    PendingDownloadState.TriggerReader.ReadAsync(stoppingToken).AsTask()
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            finally { cts.Cancel(); }
        }

        _logger.LogInformation("[PendingDownload] Service stopping");
    }

    // ── Core download logic ────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
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
            .ToListAsync(ct);

        if (!pending.Any())
        {
            // Even with no new downloads, parse anything not yet converted to claims
            await AutoParseAsync(parser, 0);
            return;
        }

        _logger.LogInformation("[PendingDownload] Starting run — {count} files to download", pending.Count);
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
                var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cr.PasswordEncrypted));
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
        await AutoParseAsync(parser, doneRemittance);
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

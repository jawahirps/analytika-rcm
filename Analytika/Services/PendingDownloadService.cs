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
    private static readonly TimeSpan StartupDelay    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<PendingDownloadService> _logger;

    public PendingDownloadService(IServiceProvider services, ILogger<PendingDownloadService> logger)
    {
        _services = services;
        _logger   = logger;
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
        var db    = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dha   = scope.ServiceProvider.GetRequiredService<IDhaPortalService>();
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

        var pending = await db.PortalTransactions
            .Include(t => t.Facility)
            .Where(t => !t.FileDownloaded && t.FileId != null && t.Portal == "DHA")
            .OrderBy(t => t.FacilityId)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

        if (!pending.Any())
        {
            _logger.LogDebug("[PendingDownload] Nothing pending — skipping run");
            return;
        }

        _logger.LogInformation("[PendingDownload] Starting run — {count} files to download", pending.Count);
        PendingDownloadState.Start(pending.Count);

        // Cache credentials per facility to avoid repeated DB round-trips
        var credCache = new Dictionary<int, (string username, string pwd)>();
        int done = 0, failed = 0;

        foreach (var tx in pending)
        {
            if (ct.IsCancellationRequested) break;

            // Resolve credentials
            if (!credCache.TryGetValue(tx.FacilityId, out var cred))
            {
                var cr = await db.PortalCredentials
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.FacilityId == tx.FacilityId
                                           && c.IsActive
                                           && c.Portal == "DHA", ct);
                if (cr == null)
                {
                    _logger.LogWarning("[PendingDownload] No DHA credentials for facility {fid} — skipping", tx.FacilityId);
                    failed++;
                    continue;
                }
                try
                {
                    var pwd = Encoding.UTF8.GetString(Convert.FromBase64String(cr.PasswordEncrypted));
                    cred = (cr.Username, pwd);
                    credCache[tx.FacilityId] = cred;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PendingDownload] Failed to decode password for facility {fid}", tx.FacilityId);
                    failed++;
                    continue;
                }
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
                            .SetProperty(t => t.FileDownloaded,   true)
                            .SetProperty(t => t.FileContentXml,   contentXml)
                            .SetProperty(t => t.FileSizeBytes,    (long?)dlBytes.Length)
                            .SetProperty(t => t.FileDownloadedAt, DateTime.UtcNow), ct);
                    done++;
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
                PendingDownloadState.Update(done, failed, tx.Facility?.Name ?? $"Facility {tx.FacilityId}");
        }

        cache.Remove("statusbar_static");
        PendingDownloadState.Finish(done, failed);
        _logger.LogInformation("[PendingDownload] Run complete — {done} downloaded, {failed} failed / {total} total",
            done, failed, pending.Count);
    }
}

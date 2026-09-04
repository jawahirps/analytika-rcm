using System.Diagnostics;

namespace Analytika.Services;

/// <summary>
/// Low-impact incremental portal maintenance. One run fetches recent records,
/// downloads only missing files, parses only unparsed transactions, then matches.
/// Runs never overlap and defer while reports or host pressure are active.
/// </summary>
public sealed class HourlyLiveDataService : BackgroundService
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HourlyLiveDataService> _logger;

    public HourlyLiveDataService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<HourlyLiveDataService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(_configuration.GetValue("LiveDataSync:IntervalMinutes", 60), 15, 1440));
        var initialDelay = TimeSpan.FromMinutes(Math.Clamp(_configuration.GetValue("LiveDataSync:InitialDelayMinutes", 2), 0, 60));
        if (initialDelay > TimeSpan.Zero)
            await Task.Delay(initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunWhenCapacityAllowsAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunWhenCapacityAllowsAsync(CancellationToken ct)
    {
        if (!await RunGate.WaitAsync(0, ct))
        {
            _logger.LogInformation("[LiveDataSync] Previous cycle is still active; duplicate cycle skipped.");
            return;
        }

        try
        {
            var retryDelay = TimeSpan.FromMinutes(Math.Clamp(_configuration.GetValue("LiveDataSync:BusyRetryMinutes", 10), 2, 30));
            while (!ct.IsCancellationRequested && IsBusy())
            {
                _logger.LogInformation("[LiveDataSync] Host is busy; deferring incremental fetch for {Delay} minutes.", retryDelay.TotalMinutes);
                await Task.Delay(retryDelay, ct);
            }

            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<PortalSyncService>();
            var parser = scope.ServiceProvider.GetRequiredService<XmlParsingService>();
            var timer = Stopwatch.StartNew();

            _logger.LogInformation("[LiveDataSync] Starting hourly incremental fetch, parse, and match cycle.");
            await sync.RunDailyDhaSyncAsync();
            var parsed = await parser.ParseDownloadedXmlAsync(null, rebuild: false, ct: ct);
            _logger.LogInformation(
                "[LiveDataSync] Cycle completed in {Elapsed}: {FilesParsed} file(s) parsed, {RecordsSaved} row(s) saved, {Matched} claim reference(s) matched.",
                timer.Elapsed, parsed.FilesParsed, parsed.RecordsSaved, parsed.MatchedClaimRefs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LiveDataSync] Incremental cycle failed; the next scheduled cycle will retry without duplicating stored records.");
        }
        finally
        {
            RunGate.Release();
        }
    }

    private static bool IsBusy()
    {
        if (ReportGenerationState.Get().IsRunning)
            return true;

        var memory = GC.GetGCMemoryInfo();
        if (memory.HighMemoryLoadThresholdBytes > 0 && memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes * 0.85)
            return true;

        return ThreadPool.PendingWorkItemCount > Math.Max(64, Environment.ProcessorCount * 16);
    }
}

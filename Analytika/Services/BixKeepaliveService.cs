using System.Net.Http;

namespace Analytika.Services;

/// <summary>
/// Internal heartbeat: pings the local <c>/healthz</c> endpoint every minute.
/// Keeps the ASP.NET request pipeline warm and feeds the Cloudflare tunnel
/// a steady stream of traffic so the connector doesn't idle-disconnect.
/// </summary>
public sealed class BixKeepaliveService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BixKeepaliveService> _logger;
    private readonly IConfiguration _config;

    public BixKeepaliveService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<BixKeepaliveService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Allow the host to finish startup before the first ping.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        var url = _config.GetValue<string>("Keepalive:Url") ?? "http://127.0.0.1:5000/healthz";
        var interval = TimeSpan.FromSeconds(_config.GetValue("Keepalive:IntervalSeconds", 60));

        using var client = _httpFactory.CreateClient("bix-keepalive");
        client.Timeout = TimeSpan.FromSeconds(8);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync(url, stoppingToken);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Keepalive ping returned {Status}", (int)resp.StatusCode);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Keepalive ping failed (non-fatal)");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

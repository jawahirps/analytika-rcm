using Microsoft.EntityFrameworkCore;
using Analytika.Models;

namespace Analytika.Services;

/// <summary>
/// Pre-loads the local Ollama model into VRAM shortly after startup and keeps it
/// pinned, so users never pay the slow cold-load (the model was being evicted on
/// the 5-min idle timeout / app restarts, causing 120s+ timeouts on the first
/// question). Fire-and-forget and fully fault-tolerant: if Ollama is down or
/// disabled, it just logs and moves on.
/// </summary>
public class OllamaWarmupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOllamaClient _ollama;
    private readonly ILogger<OllamaWarmupService> _log;

    public OllamaWarmupService(IServiceScopeFactory scopeFactory, IOllamaClient ollama, ILogger<OllamaWarmupService> log)
    {
        _scopeFactory = scopeFactory;
        _ollama = ollama;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish booting first.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var map = await db.SystemSettings.AsNoTracking()
                    .Where(s => s.Category == "Ollama")
                    .ToDictionaryAsync(s => s.Key, s => s.Value, stoppingToken);

                bool enabled = !map.TryGetValue("Enabled", out var en) || !bool.TryParse(en, out var eb) || eb;
                var baseUrl = map.TryGetValue("BaseUrl", out var bu) && !string.IsNullOrWhiteSpace(bu) ? bu!.Trim() : "http://localhost:11434";
                var model = map.TryGetValue("Model", out var m) && !string.IsNullOrWhiteSpace(m) ? m!.Trim() : "qwen2.5:7b-instruct";

                if (enabled && await _ollama.IsAvailableAsync(baseUrl, stoppingToken))
                {
                    // A tiny prompt loads the model; keep_alive=-1 (inside the client)
                    // pins it in VRAM. Generous timeout — this is the ONE cold load.
                    // Match the request-time num_ctx (4096) so the model is loaded
                    // with the right context and isn't reloaded on the first query.
                    await _ollama.ChatAsync(baseUrl, model, "You are a warm-up ping.", "ok",
                        null, 0.1, 4096, TimeSpan.FromMinutes(5), stoppingToken);
                    _log.LogInformation("Ollama model '{Model}' pre-warmed and pinned in VRAM.", model);
                }
                else
                {
                    _log.LogInformation("Ollama warm-up skipped (disabled or server unavailable).");
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Ollama warm-up attempt failed; will retry.");
            }

            // Re-warm periodically as a safety net (cheap when already loaded).
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch { return; }
        }
    }
}

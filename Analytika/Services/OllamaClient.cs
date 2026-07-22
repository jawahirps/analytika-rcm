using System.Text;
using System.Text.Json;

namespace Analytika.Services;

/// <summary>
/// Minimal client for a local Ollama server (native REST API).
/// Supports structured output via the `format` JSON-schema parameter
/// (used for reliable intent/parameter extraction) and NDJSON streaming.
/// </summary>
public interface IOllamaClient
{
    Task<bool> IsAvailableAsync(string baseUrl, CancellationToken ct = default);
    /// <summary>Non-streaming chat. When <paramref name="formatSchema"/> is set, Ollama constrains output to that JSON schema.</summary>
    Task<(string content, int tokens)> ChatAsync(string baseUrl, string model, string system, string user,
        object? formatSchema, double temperature, int numCtx, TimeSpan timeout, CancellationToken ct = default);
    /// <summary>Streaming chat; invokes onToken per content delta. Returns total token count when reported.</summary>
    Task<int> ChatStreamAsync(string baseUrl, string model, string system, string user,
        double temperature, int numCtx, TimeSpan timeout, Func<string, Task> onToken, CancellationToken ct = default);
}

public class OllamaClient : IOllamaClient
{
    private readonly IHttpClientFactory _httpFactory;
    public OllamaClient(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public async Task<bool> IsAvailableAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            var client = _httpFactory.CreateClient("ollama");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await client.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<(string content, int tokens)> ChatAsync(string baseUrl, string model, string system, string user,
        object? formatSchema, double temperature, int numCtx, TimeSpan timeout, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            // keep_alive = -1 pins the model in VRAM indefinitely, so users never
            // pay the slow cold-load after the 5-min idle unload (the main cause
            // of intermittent "assistant failed / timed out").
            ["keep_alive"] = -1,
            ["messages"] = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            ["options"] = new { temperature, num_ctx = numCtx }
        };
        if (formatSchema != null) payload["format"] = formatSchema;

        var client = _httpFactory.CreateClient("ollama");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        using var resp = await client.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 300)]}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var content = root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c)
            ? c.GetString() ?? "" : "";
        var tokens = 0;
        if (root.TryGetProperty("prompt_eval_count", out var pe) && pe.ValueKind == JsonValueKind.Number) tokens += pe.GetInt32();
        if (root.TryGetProperty("eval_count", out var ev) && ev.ValueKind == JsonValueKind.Number) tokens += ev.GetInt32();
        return (content, tokens);
    }

    public async Task<int> ChatStreamAsync(string baseUrl, string model, string system, string user,
        double temperature, int numCtx, TimeSpan timeout, Func<string, Task> onToken, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            stream = true,
            keep_alive = -1,   // pin model in VRAM (see ChatAsync)
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            options = new { temperature, num_ctx = numCtx }
        };

        var client = _httpFactory.CreateClient("ollama");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cts.Token);
            throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {err[..Math.Min(err.Length, 300)]}");
        }

        var tokens = 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)   // NDJSON: one JSON object per line
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                {
                    var piece = c.GetString();
                    if (!string.IsNullOrEmpty(piece)) await onToken(piece);
                }
                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    if (root.TryGetProperty("prompt_eval_count", out var pe) && pe.ValueKind == JsonValueKind.Number) tokens += pe.GetInt32();
                    if (root.TryGetProperty("eval_count", out var ev) && ev.ValueKind == JsonValueKind.Number) tokens += ev.GetInt32();
                    break;
                }
            }
        }
        return tokens;
    }
}

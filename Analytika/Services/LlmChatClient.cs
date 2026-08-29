using System.Text;
using System.Text.Json;

namespace Analytika.Services;

/// <summary>
/// Chat transport for the assistant and the denial analyst.
///
/// Replaces the local Ollama client: the same three operations, served by the configured
/// cloud provider (NVIDIA's OpenAI-compatible endpoint). Only the transport moved — the
/// assistant still runs its two-stage intent catalogue, so the model continues to choose
/// an approved intent rather than write SQL, which is what makes it safe for
/// facility-scoped users.
/// </summary>
public interface ILlmChatClient
{
    /// <summary>True when a provider is configured and reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Non-streaming chat. When <paramref name="formatSchema"/> is set the reply is
    /// requested as JSON — used by the assistant's structured intent-extraction stage.
    /// </summary>
    Task<(string content, int tokens)> ChatAsync(string system, string user,
        object? formatSchema, double temperature, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Streaming chat; invokes onToken per content delta. Returns total tokens when reported.</summary>
    Task<int> ChatStreamAsync(string system, string user,
        double temperature, TimeSpan timeout, Func<string, Task> onToken, CancellationToken ct = default);
}

public class LlmChatClient : ILlmChatClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiSettingsService _settings;
    private readonly ILogger<LlmChatClient> _logger;

    public LlmChatClient(IHttpClientFactory httpFactory, IAiSettingsService settings, ILogger<LlmChatClient> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    private async Task<(HttpClient http, string url, string model)?> PrepareAsync()
    {
        var s = await _settings.GetAsync();
        var key = await _settings.GetApiKeyAsync();
        if (!s.Enabled || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(s.ApiBaseUrl))
            return null;

        var http = _httpFactory.CreateClient("nvidia");
        http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return (http, s.ApiBaseUrl!.TrimEnd('/') + "/chat/completions", s.Model ?? "");
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var s = await _settings.GetAsync();
        return s.Enabled && s.HasApiKey && !string.IsNullOrWhiteSpace(s.Model);
    }

    public async Task<(string content, int tokens)> ChatAsync(string system, string user,
        object? formatSchema, double temperature, TimeSpan timeout, CancellationToken ct = default)
    {
        var prep = await PrepareAsync();
        if (prep is null) return ("", 0);
        var (http, url, model) = prep.Value;

        // The OpenAI-compatible surface takes json_object rather than Ollama's raw schema.
        // The schema itself is restated in the system prompt because not every model on the
        // catalogue honours response_format, and a malformed reply is caught by the caller's
        // deterministic validation either way.
        var sys = formatSchema is null
            ? system
            : system + "\n\nReply with JSON only, matching this schema exactly:\n"
                     + JsonSerializer.Serialize(formatSchema);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = user },
            },
        };
        if (formatSchema is not null) payload["response_format"] = new { type = "json_object" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(url, body, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Chat completion returned {Status}", (int)resp.StatusCode);
                return ("", 0);
            }

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
                             .GetProperty("content").GetString() ?? "";
            var tokens = doc.RootElement.TryGetProperty("usage", out var u)
                         && u.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : 0;
            return (content, tokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat completion failed");
            return ("", 0);
        }
    }

    public async Task<int> ChatStreamAsync(string system, string user,
        double temperature, TimeSpan timeout, Func<string, Task> onToken, CancellationToken ct = default)
    {
        var prep = await PrepareAsync();
        if (prep is null) return 0;
        var (http, url, model) = prep.Value;

        var payload = new
        {
            model,
            temperature,
            stream = true,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tokens = 0;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Streaming chat returned {Status}", (int)resp.StatusCode);
                return 0;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0 || data == "[DONE]") { if (data == "[DONE]") break; continue; }

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    if (choices[0].TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var c)
                        && c.GetString() is { Length: > 0 } piece)
                    {
                        tokens++;
                        await onToken(piece);
                    }
                }
                catch (JsonException) { /* keep-alive or partial frame — ignore */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Streaming chat failed");
        }

        return tokens;
    }
}

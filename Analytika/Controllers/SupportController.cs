using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Analytika.Controllers;

[Authorize]
public class SupportController : Controller
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<SupportController> _logger;

    private const string SystemPrompt = """
        You are the built-in support assistant for Analytika RCM — a healthcare Revenue Cycle Management
        platform used by medical billing teams in the UAE (DHA / RHA payers). The app is ASP.NET Core 10
        with SQLite/PostgreSQL.

        Core modules:
        - Dashboard: claim summary KPIs
        - Portal: auto-sync with DHA / RHA insurance portals
        - Report Scheduler: async Excel/PDF report generation
        - Resubmission: denied-claim workflow
        - Admin: user management, facilities, payers, clinicians

        Your job:
        1. If the user describes a BUG — diagnose clearly, explain the root cause, and give actionable steps to reproduce or fix it. Mark your reply with 🐛 Bug.
        2. If the user has a HOW-TO question — answer concisely. Mark with ❓ Question.
        3. If it's a FEATURE REQUEST — acknowledge and note it will be logged for the dev team. Mark with 💡 Feature Request.
        4. If it's unclear — ask a clarifying question.

        Keep replies short and focused. Use plain language (no jargon). If you're not sure, say so honestly.
        """;

    public SupportController(IHttpClientFactory http, IConfiguration config, ILogger<SupportController> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Chat([FromBody] ChatRequest req)
    {
        if (req?.Messages == null || req.Messages.Count == 0)
            return BadRequest(new { error = "No messages provided." });

        var apiKey = _config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Anthropic:ApiKey not configured");
            return StatusCode(503, new { error = "Support chat is not configured. Please contact your administrator." });
        }

        var client = _http.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var payload = new
        {
            model = "claude-sonnet-4-6",
            max_tokens = 1024,
            system = SystemPrompt,
            messages = req.Messages.Select(m => new { role = m.Role, content = m.Content }).ToList()
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync("https://api.anthropic.com/v1/messages", content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Anthropic API");
            return StatusCode(502, new { error = "Could not reach the support service. Please try again." });
        }

        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Anthropic API returned {Status}: {Body}", (int)response.StatusCode, body);
            return StatusCode((int)response.StatusCode, new { error = "Support service error. Please try again later." });
        }

        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? "";

        return Ok(new { reply = text });
    }

    public class ChatRequest
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];
    }

    public class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";
        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }
}

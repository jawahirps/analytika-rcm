using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Analytika.Controllers;

/// <summary>
/// In-app developer console for the dev build. Gated three ways: the Development
/// environment, the DevCli:Enabled switch, and the Admin role. Any of them missing and the
/// route 404s — the action never runs, so this cannot be reached on the production host
/// even if the assembly is deployed there.
/// </summary>
[Authorize(Roles = AppRoles.Admin)]
public class DevCliController : Controller
{
    private readonly IDevCliService _cli;
    private readonly IDevChatService _chat;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    public DevCliController(IDevCliService cli, IDevChatService chat, IWebHostEnvironment env, IConfiguration config)
    {
        _cli = cli;
        _chat = chat;
        _env = env;
        _config = config;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_env.IsDevelopment() || !_config.GetValue("DevCli:Enabled", false))
            context.Result = NotFound();

        base.OnActionExecuting(context);
    }

    public IActionResult Index()
    {
        ViewData["Commands"] = _cli.Commands;
        ViewData["ChatConfigured"] =
            !string.IsNullOrWhiteSpace(_config["Anthropic:ApiKey"])
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        return View();
    }

    public sealed record RunRequest(string Command);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Run([FromBody] RunRequest request, CancellationToken ct)
    {
        var result = await _cli.ExecuteAsync(request?.Command ?? string.Empty, User, ct);
        return Json(new { output = result.Output, isError = result.IsError, clear = result.ClearScreen });
    }

    public sealed record ChatTurnDto(string Role, string Text);
    public sealed record ChatRequest(string Message, string? Page, List<ChatTurnDto>? History);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        var history = (request?.History ?? new List<ChatTurnDto>())
            .Select(t => new DevChatTurn(t.Role ?? "user", t.Text ?? string.Empty))
            .ToList();

        var reply = await _chat.SendAsync(history, request?.Message ?? string.Empty, request?.Page, User, ct);

        return Json(new
        {
            text = reply.Text,
            error = reply.Error,
            tools = reply.ToolCalls.Select(t => new { command = t.Command, output = t.Output }),
        });
    }
}

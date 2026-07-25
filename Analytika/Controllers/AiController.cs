using System.Security.Claims;
using System.Text.Json;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Controllers;

[Authorize(Roles = AppRoles.RcmAccess)]
public class AiController : Controller
{
    private readonly INvidiaAnalystService _analyst;
    private readonly IAiSettingsService _settings;
    private readonly IBixAssistantService _bix;
    private readonly Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser> _userManager;
    private readonly Models.AppDbContext _db;
    private readonly FacilityScopeService _scope;

    public AiController(INvidiaAnalystService analyst, IAiSettingsService settings,
        IBixAssistantService bix,
        Microsoft.AspNetCore.Identity.UserManager<Models.ApplicationUser> userManager,
        Models.AppDbContext db, FacilityScopeService scope)
    {
        _analyst = analyst;
        _settings = settings;
        _bix = bix;
        _userManager = userManager;
        _db = db;
        _scope = scope;
    }

    private const string AnalystScopeMsg =
        "The advanced data analyst runs unrestricted queries, so it's limited to administrators. " +
        "Your account is scoped to specific facilities — please use the standard reports, which are filtered to your facilities.";

    // Facility-type users are hard-scoped to their facility; Global users see all.
    // The scope comes from the authenticated session — never from the model or client.
    private async Task<int?> GetUserFacilityIdAsync()
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser?.UserType != "Facility") return null;
        return await _db.Set<Models.UserFacility>()
            .Where(x => x.UserId == appUser.Id)
            .Select(x => (int?)x.FacilityId)
            .FirstOrDefaultAsync();
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var s = await _settings.GetAsync();
        var ollamaUp = await _bix.IsEnabledAndAvailableAsync(HttpContext.RequestAborted);
        ViewBag.Enabled = ollamaUp || (s.Enabled && s.HasApiKey);
        ViewBag.Model = ollamaUp ? "BIX local assistant (Ollama)" : s.Model;
        ViewBag.Redacted = true;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask(string question)
    {
        // Facility isolation: the NVIDIA analyst emits unrestricted SQL, so only admins may
        // use it. Scoped users get their (facility-filtered) answers via the streaming
        // local assistant instead.
        if (!await _scope.IsUnrestrictedAsync(User))
            return Json(new { ok = false, error = AnalystScopeMsg });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.Identity?.Name;
        var result = await _analyst.AskAsync(question ?? "", userId, userName, HttpContext.RequestAborted);

        return Json(new
        {
            ok = result.Success,
            answer = result.Answer,
            sql = result.Sql,
            columns = result.Columns,
            rows = result.Rows,
            rowCount = result.RowCount,
            redacted = result.Redacted,
            tokens = result.TotalTokens,
            error = result.Error
        });
    }

    // Server-Sent Events: streams progress stages, the executed query + rows, then
    // the answer token-by-token for a ChatGPT-style live response.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task AskStream(string question)
    {
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userName = User.Identity?.Name;
        var ct = HttpContext.RequestAborted;

        async Task EmitAsync(object payload)
        {
            if (ct.IsCancellationRequested) return;
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        // Provider dispatch: prefer the LOCAL Ollama intent-catalogue assistant when
        // the server is up (zero API cost, data stays on-prem); fall back to the
        // NVIDIA text-to-SQL analyst otherwise. Same SSE contract either way.
        if (await _bix.IsEnabledAndAvailableAsync(ct))
        {
            var facilityId = await GetUserFacilityIdAsync();
            await _bix.AskStreamAsync(question ?? "", userId, userName, facilityId, EmitAsync, ct);
            return;
        }

        // NVIDIA fallback is unscoped — restrict it to admins.
        if (!await _scope.IsUnrestrictedAsync(User))
        {
            await EmitAsync(new { stage = "error", error = AnalystScopeMsg });
            return;
        }
        await _analyst.AskStreamAsync(question ?? "", userId, userName, EmitAsync, ct);
    }
}

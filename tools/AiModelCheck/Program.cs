using Analytika.Modules;
using Analytika.Services;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

// Checks the configured AI Data Analyst model against what the provider actually serves.
//
// Production carries Model = "minimax", while NVIDIA's catalogue uses namespaced ids
// (publisher/model). This asks the provider for its model list and reports whether the
// configured id exists, plus the closest matches — so the setting is corrected to a real
// id rather than a guessed one.
//
// Reads the key through the app's own settings service and never prints it.
//
// Args: [searchTerm]   default: the configured model id

const string DataDir = @"J:\GhafAnalytika\Analytika";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = @"J:\GhafAnalytika\bix-app",
});
builder.Logging.ClearProviders();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DataDir, "dataprotection-keys")))
    .SetApplicationName("Analytika");
builder.Services.AddAnalytikaModules(builder.Configuration, Path.Combine(DataDir, "analytika.db"), false, false, false);

var app = builder.Build();
using var scope = app.Services.CreateScope();
var sp = scope.ServiceProvider;

var settingsSvc = sp.GetRequiredService<IAiSettingsService>();
var settings = await settingsSvc.GetAsync();
var key = await settingsSvc.GetApiKeyAsync();

Console.WriteLine("AI Data Analyst — configured vs. available");
Console.WriteLine(new string('=', 72));
Console.WriteLine($"  endpoint     {settings.ApiBaseUrl}");
Console.WriteLine($"  model        {settings.Model}");
Console.WriteLine($"  temperature  {settings.Temperature}");
Console.WriteLine($"  api key      {(string.IsNullOrWhiteSpace(key) ? "MISSING" : $"present ({key!.Length} chars)")}");
Console.WriteLine();

if (string.IsNullOrWhiteSpace(key))
{
    Console.WriteLine("No usable API key — cannot query the catalogue.");
    return 1;
}

var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("nvidia");
http.DefaultRequestHeaders.Authorization = new("Bearer", key);

var url = settings.ApiBaseUrl!.TrimEnd('/') + "/models";
HttpResponseMessage resp;
try { resp = await http.GetAsync(url); }
catch (Exception ex) { Console.WriteLine($"Could not reach {url} — {ex.Message}"); return 1; }

var body = await resp.Content.ReadAsStringAsync();
Console.WriteLine($"GET {url} -> {(int)resp.StatusCode}");
if (!resp.IsSuccessStatusCode)
{
    Console.WriteLine(body.Length > 400 ? body[..400] + "…" : body);
    return 1;
}

var ids = new List<string>();
using (var doc = JsonDocument.Parse(body))
{
    if (doc.RootElement.TryGetProperty("data", out var data))
        foreach (var m in data.EnumerateArray())
            if (m.TryGetProperty("id", out var id) && id.GetString() is { } s)
                ids.Add(s);
}

Console.WriteLine($"catalogue returned {ids.Count:N0} model id(s)");
Console.WriteLine();

var configured = settings.Model ?? "";
var exact = ids.Contains(configured, StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"  configured id '{configured}' exists at the provider: {(exact ? "YES" : "NO")}");

var term = args.Length > 0 ? args[0] : configured;
var near = ids.Where(i => i.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i).ToList();
Console.WriteLine();
Console.WriteLine(near.Count == 0
    ? $"  no catalogue id contains '{term}'"
    : $"  ids containing '{term}':");
foreach (var i in near.Take(20)) Console.WriteLine($"    {i}");

if (!exact)
{
    Console.WriteLine();
    Console.WriteLine("  A model id the provider does not serve makes every analyst request fail.");
    Console.WriteLine("  Set AI:Model to one of the ids above (Admin -> AI Agent).");
}

return exact ? 0 : 2;

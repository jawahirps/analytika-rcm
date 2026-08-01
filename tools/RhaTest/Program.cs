using Analytika.Models;
using Analytika.Modules;
using Analytika.Security;
using Analytika.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

// Tests stored Riayati (RHA) credentials against the live TMB API without needing a
// signed-in browser session. Reads the credential from the database, authenticates, and
// runs one small Search so we see whether real transactions come back.
//
// Prints only metadata about the secret (length/shape) — never the value itself.
//
// Args: [facilityId ...]   default: every facility with an active RHA credential

const string DataDir = @"J:\GhafAnalytika\Analytika";
var dbPath = Path.Combine(DataDir, "analytika.db");
var only = args.Select(a => int.TryParse(a, out var v) ? v : -1).Where(v => v > 0).ToList();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = @"J:\GhafAnalytika\bix-app",
});
builder.Logging.ClearProviders();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DataDir, "dataprotection-keys")))
    .SetApplicationName("Analytika");
builder.Services.AddAnalytikaModules(builder.Configuration, dbPath, false, false, false);

var app = builder.Build();
using var scope = app.Services.CreateScope();
var sp = scope.ServiceProvider;
var db = sp.GetRequiredService<AppDbContext>();
var protector = sp.GetRequiredService<ICredentialProtector>();

var creds = await db.PortalCredentials.AsNoTracking()
    .Where(c => c.Portal == "RHA" && c.IsActive)
    .Join(db.Facilities.AsNoTracking(), c => c.FacilityId, f => f.Id,
          (c, f) => new { c.FacilityId, Facility = f.Name, c.Username, c.PasswordEncrypted, c.ApiBaseUrl, c.LicenseCode })
    .ToListAsync();
if (only.Count > 0) creds = creds.Where(c => only.Contains(c.FacilityId)).ToList();

Console.WriteLine($"Testing {creds.Count} RHA credential(s) against Riayati TMB");
Console.WriteLine(new string('=', 72));

foreach (var c in creds)
{
    Console.WriteLine();
    Console.WriteLine($"[{c.FacilityId}] {c.Facility}");
    Console.WriteLine($"  username : {c.Username}");
    Console.WriteLine($"  licence  : {c.LicenseCode ?? "(none)"}");
    Console.WriteLine($"  endpoint : {c.ApiBaseUrl ?? RhaPortalService.DefaultBaseUrl}");

    string pwd;
    try { pwd = protector.Unprotect(c.PasswordEncrypted); }
    catch (Exception ex) { Console.WriteLine($"  RESULT   : cannot decrypt stored password — {ex.Message}"); continue; }
    Console.WriteLine($"  password : {pwd.Length} chars, uuid-shaped={System.Text.RegularExpressions.Regex.IsMatch(pwd, "^[0-9a-fA-F-]{36}$")}");

    // Fresh service per credential: it caches the username/password for the scope.
    var rha = sp.GetRequiredService<IRhaPortalService>();
    var baseUrl = c.ApiBaseUrl ?? RhaPortalService.DefaultBaseUrl;

    // Riayati publishes two hosts: "o-tmbapi" (onboarding) and "tmbapi" (production).
    // A credential issued for one is rejected by the other with the same generic 401,
    // so when the configured host refuses, try the sibling before blaming the values.
    var hosts = new List<string> { baseUrl };
    var sibling = baseUrl.Contains("//o-tmbapi", StringComparison.OrdinalIgnoreCase)
        ? baseUrl.Replace("//o-tmbapi", "//tmbapi", StringComparison.OrdinalIgnoreCase)
        : baseUrl.Replace("//tmbapi", "//o-tmbapi", StringComparison.OrdinalIgnoreCase);
    if (!hosts.Contains(sibling)) hosts.Add(sibling);

    string? token = null, workingHost = null;
    foreach (var host in hosts)
    {
        var (t, err) = await rha.AuthenticateAsync(c.Username, pwd, host, c.LicenseCode);
        if (err == null) { token = t; workingHost = host; Console.WriteLine($"  AUTH     : OK on {host}"); break; }
        Console.WriteLine($"  AUTH     : failed on {host} — {Short(err)}");
    }
    if (token == null) continue;
    baseUrl = workingHost!;

    static string Short(string s) => s.Length > 120 ? s[..120] + "…" : s;

    // Walk the window a month at a time. TMB caps a Search at 500 rows, so a single
    // two-year request would silently truncate — monthly chunks keep every month under
    // the cap and show which periods actually carry data.
    var months = int.TryParse(Environment.GetEnvironmentVariable("RHA_MONTHS"), out var m) ? m : 24;
    var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-(months - 1));
    Console.WriteLine($"  WINDOW   : {start:MMM yyyy} → {DateTime.Today:MMM yyyy} ({months} months, monthly chunks)");

    int totalClaims = 0, totalRems = 0, monthsWithData = 0;
    var samples = new List<string>();

    for (var i = 0; i < months; i++)
    {
        var mFrom = start.AddMonths(i);
        if (mFrom > DateTime.Today) break;
        var mTo = mFrom.AddMonths(1).AddDays(-1);
        var f = mFrom.ToString("yyyy-MM-dd");
        var t = (mTo > DateTime.Today ? DateTime.Today : mTo).ToString("yyyy-MM-dd");

        var (cl, clErr) = await rha.GetClaimsAsync(token!, baseUrl, f, t, c.LicenseCode);
        var (rm, rmErr) = await rha.GetRemittancesAsync(token!, baseUrl, f, t, c.LicenseCode);

        if (clErr != null || rmErr != null)
        {
            Console.WriteLine($"    {mFrom:MMM yyyy}: error — {clErr ?? rmErr}");
            continue;
        }

        totalClaims += cl.Count;
        totalRems += rm.Count;
        if (cl.Count + rm.Count > 0)
        {
            monthsWithData++;
            Console.WriteLine($"    {mFrom:MMM yyyy}: claims={cl.Count,4}  remittances={rm.Count,4}"
                            + (cl.Count == 500 || rm.Count == 500 ? "   ⚠ hit the 500 cap" : ""));
            foreach (var row in cl.Concat(rm).Take(2))
                if (samples.Count < 4) samples.Add($"    · {row.Type,-11} id={row.FileId} date={row.Date} counterparty={row.Payer}");
        }
    }

    Console.WriteLine($"  TOTAL    : {totalClaims:N0} claim + {totalRems:N0} remittance transaction(s) across {monthsWithData} month(s) with data");
    foreach (var s in samples) Console.WriteLine(s);

    // Zero rows can mean "no data" OR "my filters excluded everything". Separate the two
    // with unfiltered probes: GetNew takes no parameters at all, and a Search without the
    // licence filter removes the one value most likely to be wrong.
    if (totalClaims + totalRems == 0)
    {
        Console.WriteLine("  PROBE    : no rows from filtered Search — trying unfiltered calls");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("username", c.Username);
        http.DefaultRequestHeaders.TryAddWithoutValidation("password", pwd);

        async Task Probe(string label, string url)
        {
            try
            {
                var resp = await http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                var entities = System.Text.RegularExpressions.Regex.Matches(body, "\"ID\"\\s*:").Count;
                var snippet = body.Length > 220 ? body[..220] + "…" : body;
                Console.WriteLine($"    {label,-26} HTTP {(int)resp.StatusCode}  entities≈{entities}");
                Console.WriteLine($"      {snippet.Replace("\n", " ").Replace("\r", "")}");
            }
            catch (Exception ex) { Console.WriteLine($"    {label,-26} error — {ex.Message}"); }
        }

        await Probe("Claim/GetNew", $"{baseUrl}/api/Claim/GetNew");
        await Probe("Claim/Search no-licence", $"{baseUrl}/api/Claim/Search?direction=0&downloaded=2");
        await Probe("Claim/Search dir=1", $"{baseUrl}/api/Claim/Search?direction=1&downloaded=2");
        await Probe("Authorization/GetNew", $"{baseUrl}/api/Authorization/GetNew");
    }
}

Console.WriteLine();
Console.WriteLine(new string('=', 72));
Console.WriteLine("done");

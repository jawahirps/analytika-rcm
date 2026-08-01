using Analytika.Modules;
using Analytika.Services;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;

// Exercises the /DevCli command engine against the DEV database without a browser
// session, so the console's behaviour can be checked before anyone signs in to it.
//
// Runs read-only commands by default. Pass commands as arguments to run specific ones:
//   dotnet run --project tools\DevCliTest -- "db select count(*) from Facilities"

const string DevDir = @"J:\GhafAnalytika\bix-dev-data";
const string DevApp = @"J:\GhafAnalytika\bix-dev";

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = DevApp,
    EnvironmentName = "Development",
});
builder.Logging.ClearProviders();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Data:Dir"] = DevDir,
});
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(DevDir, "dataprotection-keys")))
    .SetApplicationName("Analytika");
builder.Services.AddControllersWithViews();   // DevCliService needs the action descriptors
builder.Services.AddAnalytikaModules(builder.Configuration, Path.Combine(DevDir, "analytika.db"), false, false, false);

var app = builder.Build();
using var scope = app.Services.CreateScope();
var cli = scope.ServiceProvider.GetRequiredService<IDevCliService>();

// A stand-in signed-in admin — the engine only reads the name and roles.
var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
{
    new Claim(ClaimTypes.Name, "devcli-test"),
    new Claim(ClaimTypes.Role, "Admin"),
}, "test"));

var commands = args.Length > 0 ? args : new[]
{
    "help",
    "about",
    "whoami",
    "tables",
    "schema Facilities",
    "db SELECT Id, Name, LicenseCode FROM Facilities ORDER BY Id",
    "db DELETE FROM Facilities",             // must be refused
    "db SELECT 1; DROP TABLE Facilities",    // must be refused
    "sync",
    "facilities",
    "creds",
    "users",
    "settings",
    "routes DevCli",
    "logs 5",
    "notes",
    "bogus",                                  // must report unknown command
};

var failures = 0;
foreach (var command in commands)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"› {command}");
    Console.WriteLine(new string('-', 78));
    var result = await cli.ExecuteAsync(command, user, CancellationToken.None);
    Console.WriteLine(result.Output.Length == 0 ? "(no output)" : result.Output);
    if (result.IsError) Console.WriteLine("   [reported as error]");

    // The two rejection cases and the unknown verb SHOULD be errors; nothing else should.
    var shouldFail = command.StartsWith("db DELETE") || command.Contains("DROP TABLE") || command == "bogus";
    if (shouldFail != result.IsError)
    {
        Console.WriteLine($"   *** UNEXPECTED: expected error={shouldFail}, got {result.IsError}");
        failures++;
    }
    Console.WriteLine();
}

// The Claude-backed chat on the same screen. With no API key it must report that
// clearly instead of throwing — that is the state a fresh dev box is in.
Console.WriteLine(new string('=', 78));
Console.WriteLine("› chat: \"what tables are in this database?\"");
Console.WriteLine(new string('-', 78));
var chat = scope.ServiceProvider.GetRequiredService<IDevChatService>();
var reply = await chat.SendAsync(Array.Empty<DevChatTurn>(), "what tables are in this database?",
                                 "/DevCli", user, CancellationToken.None);
foreach (var call in reply.ToolCalls) Console.WriteLine($"   [tool] {call.Command}");
Console.WriteLine(reply.Error is not null ? $"   error: {reply.Error}" : reply.Text);
Console.WriteLine();

Console.WriteLine(new string('=', 78));
Console.WriteLine(failures == 0 ? "all commands behaved as expected" : $"{failures} command(s) misbehaved");
return failures == 0 ? 0 : 1;

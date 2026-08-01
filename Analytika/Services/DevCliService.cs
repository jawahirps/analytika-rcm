using Analytika.Models;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Analytika.Services;

/// <summary>
/// Command engine behind the in-app developer console (/DevCli). Exists so the dev build
/// can be inspected and driven from the same screen it is being tested on — checking a
/// query, tailing the log, or filing a change request without leaving the browser.
///
/// Every command is read-only against application data. The two exceptions are explicit
/// and narrow: <c>set</c> writes a SystemSettings row, and <c>note</c> appends to a
/// request file. There is no shell, no file write outside the data directory, and no way
/// to reach production — the controller only serves in the Development environment.
/// </summary>
public interface IDevCliService
{
    Task<DevCliResult> ExecuteAsync(string input, ClaimsPrincipal user, CancellationToken ct);
    IReadOnlyList<DevCliCommand> Commands { get; }
}

public sealed record DevCliResult(string Output, bool IsError = false, bool ClearScreen = false);

public sealed record DevCliCommand(string Name, string Usage, string Summary);

public sealed class DevCliService : IDevCliService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IActionDescriptorCollectionProvider _actions;
    private readonly ILogger<DevCliService> _logger;

    private const int MaxRows = 200;      // hard cap on rows returned by `db`
    private const int MaxCellWidth = 60;  // truncate wide values so the grid stays readable

    public DevCliService(
        AppDbContext db,
        IWebHostEnvironment env,
        IConfiguration config,
        IActionDescriptorCollectionProvider actions,
        ILogger<DevCliService> logger)
    {
        _db = db;
        _env = env;
        _config = config;
        _actions = actions;
        _logger = logger;
    }

    public IReadOnlyList<DevCliCommand> Commands { get; } = new[]
    {
        new DevCliCommand("help",       "help [command]",              "This list, or detail for one command"),
        new DevCliCommand("about",      "about",                       "Environment, port, data directory, database size, uptime"),
        new DevCliCommand("tables",     "tables [filter]",             "Tables with row counts"),
        new DevCliCommand("schema",     "schema <table>",              "Columns and indexes of a table"),
        new DevCliCommand("db",         "db <select …>",               "Run a read-only query (SELECT/WITH/EXPLAIN only)"),
        new DevCliCommand("sync",       "sync",                        "Download and parse backlog by portal"),
        new DevCliCommand("facilities", "facilities",                  "Facilities with tenant and portal credentials"),
        new DevCliCommand("creds",      "creds",                       "Portal credentials — shape only, never values"),
        new DevCliCommand("users",      "users [filter]",              "Accounts with roles and active flag"),
        new DevCliCommand("settings",   "settings [category]",         "SystemSettings rows (secrets masked)"),
        new DevCliCommand("set",        "set <category> <key> <value>","Write one SystemSettings value"),
        new DevCliCommand("routes",     "routes [filter]",             "Mapped controller actions"),
        new DevCliCommand("logs",       "logs [lines] [filter]",       "Tail this instance's log file"),
        new DevCliCommand("note",       "note <text>",                 "File a change request for the build to pick up"),
        new DevCliCommand("notes",      "notes [done <n> | clear]",    "List or resolve filed requests"),
        new DevCliCommand("whoami",     "whoami",                      "Signed-in account, roles, tenant"),
        new DevCliCommand("clear",      "clear",                       "Clear the console"),
    };

    public async Task<DevCliResult> ExecuteAsync(string input, ClaimsPrincipal user, CancellationToken ct)
    {
        input = (input ?? string.Empty).Trim();
        if (input.Length == 0) return new DevCliResult(string.Empty);

        var space = input.IndexOf(' ');
        var verb = (space < 0 ? input : input[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : input[(space + 1)..].Trim();

        try
        {
            return verb switch
            {
                "help" or "?"           => Help(rest),
                "about" or "env"        => await AboutAsync(ct),
                "tables"                => await TablesAsync(rest, ct),
                "schema"                => await SchemaAsync(rest, ct),
                "db" or "sql" or "query"=> await QueryAsync(rest, ct),
                "sync"                  => await SyncAsync(ct),
                "facilities"            => await FacilitiesAsync(ct),
                "creds"                 => await CredsAsync(ct),
                "users"                 => await UsersAsync(rest, ct),
                "settings"              => await SettingsAsync(rest, ct),
                "set"                   => await SetSettingAsync(rest, ct),
                "routes"                => Routes(rest),
                "logs" or "log"         => Logs(rest),
                "note"                  => Note(rest, user),
                "notes"                 => Notes(rest),
                "whoami"                => WhoAmI(user),
                "clear" or "cls"        => new DevCliResult(string.Empty, ClearScreen: true),
                _                       => new DevCliResult($"Unknown command '{verb}'. Type 'help' for the list.", IsError: true),
            };
        }
        catch (OperationCanceledException)
        {
            return new DevCliResult("Cancelled.", IsError: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DevCli command failed: {Input}", input);
            return new DevCliResult($"{ex.GetType().Name}: {ex.Message}", IsError: true);
        }
    }

    // ── help ──────────────────────────────────────────────────────────────

    private DevCliResult Help(string arg)
    {
        if (!string.IsNullOrWhiteSpace(arg))
        {
            var one = Commands.FirstOrDefault(c => c.Name.Equals(arg.Trim(), StringComparison.OrdinalIgnoreCase));
            return one is null
                ? new DevCliResult($"No command named '{arg}'.", IsError: true)
                : new DevCliResult($"{one.Usage}\n  {one.Summary}");
        }

        var sb = new StringBuilder();
        var width = Commands.Max(c => c.Usage.Length);
        foreach (var c in Commands)
            sb.AppendLine($"  {c.Usage.PadRight(width)}   {c.Summary}");
        sb.AppendLine();
        sb.AppendLine("  Up/Down recalls history. Ctrl+L clears. Queries are capped at "
                      + $"{MaxRows} rows and run on a read-only connection.");
        return new DevCliResult(sb.ToString().TrimEnd());
    }

    // ── about ─────────────────────────────────────────────────────────────

    private async Task<DevCliResult> AboutAsync(CancellationToken ct)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var uptime = DateTime.Now - proc.StartTime;
        var dbPath = DbPath();
        var dbSize = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
        var walPath = dbPath + "-wal";
        var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

        var urls = _config["Kestrel:Endpoints:Http:Url"]
                   ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                   ?? "(host default)";

        var sb = new StringBuilder();
        sb.AppendLine($"  environment   {_env.EnvironmentName}");
        sb.AppendLine($"  listening     {urls}");
        sb.AppendLine($"  content root  {_env.ContentRootPath}");
        sb.AppendLine($"  data dir      {DataDir()}");
        sb.AppendLine($"  database      {dbPath}");
        sb.AppendLine($"                {Bytes(dbSize)}  (wal {Bytes(walSize)})");
        sb.AppendLine($"  provider      {_db.Database.ProviderName}");
        sb.AppendLine($"  process       pid {proc.Id}, up {Duration(uptime)}");
        sb.AppendLine($"  build         {File.GetLastWriteTime(typeof(DevCliService).Assembly.Location):yyyy-MM-dd HH:mm}");
        sb.AppendLine($"  machine       {Environment.MachineName}  .NET {Environment.Version}");

        var open = ReadNotes().Count(n => n.Status == "open");
        if (open > 0) sb.AppendLine($"  requests      {open} open — type 'notes'");

        await Task.CompletedTask;
        return new DevCliResult(sb.ToString().TrimEnd());
    }

    // ── schema exploration ────────────────────────────────────────────────

    private async Task<DevCliResult> TablesAsync(string filter, CancellationToken ct)
    {
        await using var conn = OpenReadOnly();
        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        }

        if (!string.IsNullOrWhiteSpace(filter))
            names = names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) return new DevCliResult("No matching tables.");

        var rows = new List<string[]>();
        foreach (var n in names)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{n.Replace("\"", "\"\"")}\"";
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            rows.Add(new[] { n, count.ToString("N0") });
        }

        return new DevCliResult(Grid(new[] { "table", "rows" }, rows));
    }

    private async Task<DevCliResult> SchemaAsync(string table, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(table))
            return new DevCliResult("Usage: schema <table>", IsError: true);
        if (!Regex.IsMatch(table.Trim(), @"^[A-Za-z_][A-Za-z0-9_]*$"))
            return new DevCliResult("Table names are letters, digits and underscores only.", IsError: true);

        table = table.Trim();
        await using var conn = OpenReadOnly();

        var cols = new List<string[]>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                cols.Add(new[]
                {
                    r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.GetInt32(3) == 1 ? "NOT NULL" : "",
                    r.IsDBNull(4) ? "" : r.GetValue(4).ToString() ?? "",
                    r.GetInt32(5) == 1 ? "PK" : "",
                });
        }
        if (cols.Count == 0) return new DevCliResult($"No table named '{table}'.", IsError: true);

        var sb = new StringBuilder();
        sb.AppendLine(Grid(new[] { "column", "type", "null", "default", "key" }, cols));

        var idx = new List<string[]>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list(\"{table}\")";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                idx.Add(new[] { r.GetString(1), r.GetInt32(2) == 1 ? "unique" : "" });
        }
        if (idx.Count > 0)
        {
            // index_list only names them; the columns are what actually matters when
            // working out whether a query can be served by an index.
            var detailed = new List<string[]>();
            foreach (var i in idx)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA index_info(\"{i[0]}\")";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                var parts = new List<string>();
                while (await r.ReadAsync(ct)) parts.Add(r.IsDBNull(2) ? "?" : r.GetString(2));
                detailed.Add(new[] { i[0], string.Join(", ", parts), i[1] });
            }
            sb.AppendLine();
            sb.AppendLine(Grid(new[] { "index", "columns", "" }, detailed));
        }

        return new DevCliResult(sb.ToString().TrimEnd());
    }

    // ── read-only query ───────────────────────────────────────────────────

    private static readonly Regex AllowedStart =
        new(@"^\s*(select|with|explain|values)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // SQLite allows a CTE to carry a writing statement (WITH … INSERT). The read-only
    // connection below already makes that fail, but rejecting it here gives a clear message
    // instead of a driver error.
    private static readonly Regex Forbidden =
        new(@"\b(insert|update|delete|drop|alter|create|replace|attach|detach|vacuum|reindex|pragma)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<DevCliResult> QueryAsync(string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return new DevCliResult("Usage: db <select …>", IsError: true);

        sql = sql.Trim().TrimEnd(';');
        if (sql.Contains(';'))
            return new DevCliResult("One statement at a time.", IsError: true);
        if (!AllowedStart.IsMatch(sql))
            return new DevCliResult("Only SELECT, WITH, VALUES and EXPLAIN are accepted here.", IsError: true);
        if (Forbidden.IsMatch(sql))
            return new DevCliResult("That statement contains a writing or schema keyword.", IsError: true);

        // An unbounded SELECT on a table with millions of rows would stream forever; cap it
        // unless the caller has already said how much they want.
        var capped = Regex.IsMatch(sql, @"\blimit\b", RegexOptions.IgnoreCase)
            ? sql
            : $"{sql} LIMIT {MaxRows}";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var conn = OpenReadOnly();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = capped;
        cmd.CommandTimeout = 60;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var headers = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct) && rows.Count < MaxRows)
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.IsDBNull(i) ? "" : reader.GetValue(i);
                var s = v switch
                {
                    byte[] b => $"<{Bytes(b.Length)} blob>",
                    DateTime d => d.ToString("yyyy-MM-dd HH:mm"),
                    _ => v?.ToString() ?? "",
                };
                s = s.Replace("\r", "").Replace("\n", "⏎");
                row[i] = s.Length > MaxCellWidth ? s[..MaxCellWidth] + "…" : s;
            }
            rows.Add(row);
        }
        sw.Stop();

        var body = rows.Count == 0
            ? "(no rows)"
            : Grid(headers, rows);
        var note = rows.Count >= MaxRows ? $"  — capped at {MaxRows}" : "";
        return new DevCliResult($"{body}\n\n{rows.Count} row(s) in {sw.ElapsedMilliseconds} ms{note}");
    }

    // ── app state ─────────────────────────────────────────────────────────

    private async Task<DevCliResult> SyncAsync(CancellationToken ct)
    {
        var pending = await _db.PortalTransactions.AsNoTracking()
            .Where(t => (t.Type == "Claim" || t.Type == "Remittance") && !t.FileUnavailable)
            .GroupBy(t => new { t.Portal, Downloaded = t.FileContentXml != null })
            .Select(g => new { g.Key.Portal, g.Key.Downloaded, Count = g.Count() })
            .ToListAsync(ct);

        var rows = pending
            .GroupBy(p => p.Portal)
            .Select(g => new[]
            {
                g.Key ?? "(none)",
                g.Where(x => x.Downloaded).Sum(x => x.Count).ToString("N0"),
                g.Where(x => !x.Downloaded).Sum(x => x.Count).ToString("N0"),
            })
            .ToList();

        var parsed = await _db.XmlParsedRecords.AsNoTracking().CountAsync(ct);
        var activities = await _db.XmlParsedActivities.AsNoTracking().CountAsync(ct);
        var unavailable = await _db.PortalTransactions.AsNoTracking().CountAsync(t => t.FileUnavailable, ct);

        var sb = new StringBuilder();
        sb.AppendLine(rows.Count == 0 ? "(no transactions)" : Grid(new[] { "portal", "downloaded", "pending" }, rows));
        sb.AppendLine();
        sb.AppendLine($"  parsed records     {parsed:N0}");
        sb.AppendLine($"  parsed activities  {activities:N0}");
        sb.AppendLine($"  unavailable files  {unavailable:N0}");
        return new DevCliResult(sb.ToString().TrimEnd());
    }

    private async Task<DevCliResult> FacilitiesAsync(CancellationToken ct)
    {
        var facilities = await _db.Facilities.AsNoTracking()
            .OrderBy(f => f.Name)
            .Select(f => new { f.Id, f.Name, f.LicenseCode, f.TenantId, f.IsActive })
            .ToListAsync(ct);

        var creds = await _db.PortalCredentials.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new { c.FacilityId, c.Portal })
            .ToListAsync(ct);

        var rows = facilities.Select(f => new[]
        {
            f.Id.ToString(),
            f.Name ?? "",
            f.LicenseCode ?? "",
            f.TenantId?.ToString() ?? "—",
            f.IsActive ? "active" : "off",
            string.Join(",", creds.Where(c => c.FacilityId == f.Id).Select(c => c.Portal).Distinct().OrderBy(p => p)),
        }).ToList();

        return new DevCliResult(Grid(new[] { "id", "facility", "licence", "tenant", "state", "portals" }, rows));
    }

    private async Task<DevCliResult> CredsAsync(CancellationToken ct)
    {
        // Deliberately reports only the shape of each secret. The console never decrypts
        // or displays a stored password.
        var creds = await _db.PortalCredentials.AsNoTracking()
            .Select(c => new
            {
                c.FacilityId, c.Portal, c.Username, c.LicenseCode, c.ApiBaseUrl, c.IsActive,
                Len = c.PasswordEncrypted == null ? 0 : c.PasswordEncrypted.Length
            })
            .ToListAsync(ct);

        var names = await _db.Facilities.AsNoTracking()
            .ToDictionaryAsync(f => f.Id, f => f.Name ?? f.Id.ToString(), ct);

        var rows = creds.OrderBy(c => c.Portal).ThenBy(c => c.FacilityId).Select(c => new[]
        {
            c.Portal ?? "",
            names.TryGetValue(c.FacilityId, out var n) ? n : c.FacilityId.ToString(),
            c.Username ?? "",
            c.LicenseCode ?? "",
            c.ApiBaseUrl ?? "(default)",
            c.Len > 0 ? "stored" : "empty",
            c.IsActive ? "active" : "off",
        }).ToList();

        return new DevCliResult(Grid(new[] { "portal", "facility", "username", "licence", "endpoint", "secret", "state" }, rows));
    }

    private async Task<DevCliResult> UsersAsync(string filter, CancellationToken ct)
    {
        var users = await _db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.UserName, u.Email, u.IsActive, u.TenantId })
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(filter))
            users = users.Where(u =>
                (u.UserName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (u.Email ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var roleNames = await _db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name ?? "", ct);
        var userRoles = await _db.UserRoles.AsNoTracking().ToListAsync(ct);

        var rows = users.OrderBy(u => u.UserName).Select(u => new[]
        {
            u.UserName ?? "",
            u.Email ?? "",
            u.TenantId?.ToString() ?? "—",
            u.IsActive ? "active" : "off",
            string.Join(",", userRoles.Where(ur => ur.UserId == u.Id)
                                      .Select(ur => roleNames.TryGetValue(ur.RoleId, out var rn) ? rn : "?")
                                      .OrderBy(r => r)),
        }).ToList();

        return new DevCliResult(rows.Count == 0 ? "No matching accounts."
            : Grid(new[] { "user", "email", "tenant", "state", "roles" }, rows));
    }

    // ── settings ──────────────────────────────────────────────────────────

    private static bool IsSecretKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("token", StringComparison.OrdinalIgnoreCase);

    private async Task<DevCliResult> SettingsAsync(string category, CancellationToken ct)
    {
        var q = _db.SystemSettings.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
            q = q.Where(s => s.Category == category);

        var settings = await q.OrderBy(s => s.Category).ThenBy(s => s.Key).ToListAsync(ct);
        if (settings.Count == 0)
            return new DevCliResult(string.IsNullOrWhiteSpace(category)
                ? "No settings stored."
                : $"No settings in category '{category}'.");

        var rows = settings.Select(s => new[]
        {
            s.Category,
            s.Key,
            IsSecretKey(s.Key)
                ? (string.IsNullOrEmpty(s.Value) ? "(empty)" : $"({s.Value!.Length} chars, hidden)")
                : Truncate(s.Value ?? "", MaxCellWidth),
            s.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();

        return new DevCliResult(Grid(new[] { "category", "key", "value", "updated" }, rows));
    }

    private async Task<DevCliResult> SetSettingAsync(string args, CancellationToken ct)
    {
        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return new DevCliResult("Usage: set <category> <key> <value>", IsError: true);
        if (IsSecretKey(parts[1]))
            return new DevCliResult(
                "Secrets are not settable from the console — use Admin → Credentials so the value is encrypted at rest.",
                IsError: true);

        var (category, key, value) = (parts[0], parts[1], parts[2]);
        var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Category == category && s.Key == key, ct);
        var previous = row?.Value;

        if (row is null)
        {
            row = new SystemSetting { Category = category, Key = key };
            _db.SystemSettings.Add(row);
        }
        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("DevCli set {Category}:{Key}", category, key);
        return new DevCliResult($"{category}:{key} = {value}"
            + (previous is null ? "  (created)" : $"  (was {Truncate(previous, 40)})"));
    }

    // ── routes / logs ─────────────────────────────────────────────────────

    private DevCliResult Routes(string filter)
    {
        var rows = _actions.ActionDescriptors.Items
            .OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()
            .Select(a => new
            {
                Route = a.AttributeRouteInfo?.Template ?? $"{a.ControllerName}/{a.ActionName}",
                Controller = a.ControllerName,
                Action = a.ActionName,
                Methods = string.Join(",", a.ActionConstraints?
                    .OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                    .SelectMany(c => c.HttpMethods) ?? new[] { "GET" }),
            })
            .Where(a => string.IsNullOrWhiteSpace(filter)
                        || a.Route.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || a.Controller.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Controller).ThenBy(a => a.Action)
            .Select(a => new[] { a.Methods.Length == 0 ? "GET" : a.Methods, "/" + a.Route })
            .ToList();

        return new DevCliResult(rows.Count == 0 ? "No matching routes." : Grid(new[] { "method", "route" }, rows));
    }

    private DevCliResult Logs(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var lines = parts.Length > 0 && int.TryParse(parts[0], out var n) ? Math.Clamp(n, 1, 500) : 40;
        var filter = parts.Length > 1 ? parts[1]
                   : (parts.Length == 1 && !int.TryParse(parts[0], out _) ? parts[0] : null);

        var dir = Path.Combine(_env.ContentRootPath, "logs");
        if (!Directory.Exists(dir)) return new DevCliResult($"No log directory at {dir}.", IsError: true);

        var newest = new DirectoryInfo(dir).GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return new DevCliResult("No log files yet.", IsError: true);

        // Serilog holds the file open with a write handle, so it must be opened shared.
        using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var all = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (filter is not null && !line.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            all.Add(line);
            if (all.Count > 5000) all.RemoveAt(0);   // bound memory on a large log
        }

        var tail = all.TakeLast(lines).ToList();
        return new DevCliResult($"{newest.Name} — last {tail.Count} line(s)"
                                + (filter is null ? "" : $" matching '{filter}'")
                                + "\n\n" + string.Join("\n", tail));
    }

    // ── change requests ───────────────────────────────────────────────────

    private sealed record DevNote(string Ts, string User, string Text, string Status);

    private string NotesPath() => Path.Combine(DataDir(), "dev-requests.jsonl");

    private List<DevNote> ReadNotes()
    {
        var path = NotesPath();
        if (!File.Exists(path)) return new List<DevNote>();
        var list = new List<DevNote>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var n = JsonSerializer.Deserialize<DevNote>(line);
                if (n is not null) list.Add(n);
            }
            catch { /* a hand-edited line should not break the reader */ }
        }
        return list;
    }

    private void WriteNotes(IEnumerable<DevNote> notes) =>
        File.WriteAllLines(NotesPath(), notes.Select(n => JsonSerializer.Serialize(n)));

    private DevCliResult Note(string text, ClaimsPrincipal user)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new DevCliResult("Usage: note <what you want changed>", IsError: true);

        var notes = ReadNotes();
        notes.Add(new DevNote(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            user.Identity?.Name ?? "unknown",
            text.Trim(),
            "open"));
        WriteNotes(notes);

        _logger.LogInformation("DevCli change request filed: {Text}", text.Trim());
        return new DevCliResult($"Filed as #{notes.Count}. {notes.Count(n => n.Status == "open")} open.\n"
                                + $"  → {NotesPath()}");
    }

    private DevCliResult Notes(string args)
    {
        var notes = ReadNotes();

        if (args.StartsWith("clear", StringComparison.OrdinalIgnoreCase))
        {
            var removed = notes.Count;
            WriteNotes(Array.Empty<DevNote>());
            return new DevCliResult($"Cleared {removed} request(s).");
        }

        if (args.StartsWith("done", StringComparison.OrdinalIgnoreCase))
        {
            var arg = args[4..].Trim();
            if (!int.TryParse(arg, out var idx) || idx < 1 || idx > notes.Count)
                return new DevCliResult($"Usage: notes done <1-{notes.Count}>", IsError: true);
            notes[idx - 1] = notes[idx - 1] with { Status = "done" };
            WriteNotes(notes);
            return new DevCliResult($"#{idx} marked done.");
        }

        if (notes.Count == 0) return new DevCliResult("No requests filed. Use: note <text>");

        var rows = notes.Select((n, i) => new[]
        {
            (i + 1).ToString(),
            n.Status == "open" ? "open" : "done",
            n.Ts,
            n.User,
            Truncate(n.Text, 90),
        }).ToList();

        return new DevCliResult(Grid(new[] { "#", "state", "filed", "by", "request" }, rows));
    }

    private DevCliResult WhoAmI(ClaimsPrincipal user)
    {
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"  user    {user.Identity?.Name ?? "(anonymous)"}");
        sb.AppendLine($"  roles   {(roles.Count == 0 ? "(none)" : string.Join(", ", roles))}");
        sb.AppendLine($"  auth    {user.Identity?.AuthenticationType ?? "(none)"}");
        return new DevCliResult(sb.ToString().TrimEnd());
    }

    // ── helpers ───────────────────────────────────────────────────────────

    // Data:Dir is set by startup to the directory the app actually opened. Reading it
    // here (rather than consulting DB_DIR again) is what keeps the console's queries on
    // the same database as the rest of the app.
    private string DataDir() =>
        _config["Data:Dir"]
        ?? Environment.GetEnvironmentVariable("DB_DIR")
        ?? _env.ContentRootPath;

    private string DbPath() => Path.Combine(DataDir(), "analytika.db");

    /// <summary>
    /// A separate read-only SQLite handle. The mode flag — not the keyword filter — is what
    /// actually guarantees a console query cannot write to the database.
    /// </summary>
    private SqliteConnection OpenReadOnly()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath(),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        conn.Open();
        using var busy = conn.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=10000";
        busy.ExecuteNonQuery();
        return conn;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):N1} KB",
        _ => $"{b} B",
    };

    private static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : $"{(int)t.TotalMinutes}m {t.Seconds}s";

    /// <summary>Fixed-width text table — the console renders in a monospace font.</summary>
    private static string Grid(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = headers.Select(h => h.Length).ToArray();
        foreach (var row in rows)
            for (var i = 0; i < widths.Length && i < row.Length; i++)
                widths[i] = Math.Max(widths[i], (row[i] ?? "").Length);

        var sb = new StringBuilder();
        sb.AppendLine("  " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        sb.AppendLine("  " + string.Join("  ", widths.Select(w => new string('─', w))));
        foreach (var row in rows)
            sb.AppendLine("  " + string.Join("  ", widths.Select((w, i) =>
                (i < row.Length ? row[i] ?? "" : "").PadRight(w))).TrimEnd());
        return sb.ToString().TrimEnd();
    }
}

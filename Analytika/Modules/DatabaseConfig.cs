using Npgsql;

namespace Analytika.Modules;

/// <summary>
/// Resolves the database provider and connection string from configuration.
/// Defaults to SQLite for existing installs; selects Postgres when
/// Database:Provider=postgres or a DATABASE_URL is present (managed cloud).
/// </summary>
public static class DatabaseConfig
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";

    static DatabaseConfig()
    {
        // The app works with local-time DateTimes throughout (portal timestamps,
        // DateTime.Today ranges). Map them to 'timestamp without time zone'
        // instead of Npgsql's default UTC-only 'timestamptz'.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    public static string GetProvider(IConfiguration configuration)
    {
        var configured = configuration.GetValue<string>("Database:Provider");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var p = configured.Trim().ToLowerInvariant();
            return p is "postgres" or "postgresql" or "npgsql" ? Postgres : Sqlite;
        }
        return string.IsNullOrWhiteSpace(GetPostgresConnectionString(configuration)) ? Sqlite : Postgres;
    }

    public static string? GetPostgresConnectionString(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("Postgres")
            ?? configuration["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Railway/Render/Heroku style URL → Npgsql keyword connection string
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(raw);
            var userInfo = uri.UserInfo.Split(':', 2);
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.Port > 0 ? uri.Port : 5432,
                Database = uri.AbsolutePath.TrimStart('/'),
                Username = Uri.UnescapeDataString(userInfo[0]),
                SslMode = SslMode.Prefer
            };
            if (userInfo.Length > 1)
                builder.Password = Uri.UnescapeDataString(userInfo[1]);
            return builder.ConnectionString;
        }
        return raw;
    }
}

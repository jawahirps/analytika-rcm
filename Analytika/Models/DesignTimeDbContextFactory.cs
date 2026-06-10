using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Analytika.Models;

/// <summary>
/// Used only by `dotnet ef` at design time to generate migrations.
/// Migrations are generated against the Postgres provider — SQLite installs
/// do not use migrations (they keep the EnsureCreated + raw-SQL path).
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Keep design-time column types in sync with runtime (see DatabaseConfig)
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var conn = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Database=analytika;Username=postgres";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(conn)
            .Options;
        return new AppDbContext(options);
    }
}

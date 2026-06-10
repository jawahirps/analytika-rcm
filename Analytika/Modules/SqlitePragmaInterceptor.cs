using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Analytika.Modules;

/// <summary>
/// Applies high-throughput SQLite PRAGMAs on every pooled connection open.
/// WAL + NORMAL sync lets readers (dashboards/reports) run concurrently with the
/// background sync writer; busy_timeout removes "database is locked" failures
/// under contention; mmap + cache_size cut read latency on the large DB.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    // -16384 => 16 MB page cache per connection; 256 MB mmap window.
    private const string Pragmas =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA temp_store=MEMORY;" +
        "PRAGMA cache_size=-16384;" +
        "PRAGMA mmap_size=268435456;" +
        "PRAGMA foreign_keys=ON;" +
        "PRAGMA wal_autocheckpoint=1000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

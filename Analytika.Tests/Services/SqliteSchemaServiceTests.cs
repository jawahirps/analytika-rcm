using Analytika.Models;
using Analytika.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Analytika.Tests.Services;

public class SqliteSchemaServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public SqliteSchemaServiceTests()
    {
        // Use a shared in-memory SQLite connection that stays open for the test lifetime
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void EnsureSchema_CreatesTables()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: false);

        var tables = GetTableNames();

        tables.Should().Contain("DhpoCodingSets");
        tables.Should().Contain("SystemSettings");
        tables.Should().Contain("RemittanceClaims");
        tables.Should().Contain("XmlParsedRecords");
        tables.Should().Contain("ResubmissionTasks");
    }

    [Fact]
    public void EnsureSchema_CreatesIdentityTables()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: false);

        var tables = GetTableNames();

        // EnsureCreated should also create the EF Core / Identity tables
        tables.Should().Contain("Facilities");
        tables.Should().Contain("PortalCredentials");
        tables.Should().Contain("PortalTransactions");
        tables.Should().Contain("ReportRequests");
    }

    [Fact]
    public void EnsureSchema_MigrateColumns_AddsEmailToColumn()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: false);

        var columns = GetColumnNames("ReportRequests");

        columns.Should().Contain("EmailTo");
    }

    [Fact]
    public void EnsureSchema_MigrateColumns_AddsClaimCategoryColumn()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: false);

        var columns = GetColumnNames("RemittanceClaims");

        columns.Should().Contain("ClaimCategory");
    }

    [Fact]
    public void EnsureSchema_WithIndexes_CreatesPerformanceIndexes()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: true);

        var indexes = GetIndexNames();

        indexes.Should().Contain("IX_PortalFetchLogs_FetchedAt");
        indexes.Should().Contain("IX_PortalFetchLogs_FacilityId_Status");
        indexes.Should().Contain("IX_PortalFetchLogs_FacilityId_Operation");
        indexes.Should().Contain("IX_PortalTransactions_FacilityId_FileDownloaded");
        indexes.Should().Contain("IX_PortalTransactions_SyncedAt");
    }

    [Fact]
    public void EnsureSchema_WithIndexes_CreatesTableIndexes()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: true);

        var indexes = GetIndexNames();

        indexes.Should().Contain("IX_RemittanceClaims_FacilityId");
        indexes.Should().Contain("IX_RemittanceClaims_ClaimId");
        indexes.Should().Contain("IX_XmlParsedRecords_ClaimId");
        indexes.Should().Contain("IX_ResubmissionTasks_Status");
        indexes.Should().Contain("IX_ResubmissionTasks_AssignedToUserId");
    }

    [Fact]
    public void EnsureSchema_CalledTwice_DoesNotThrow()
    {
        SqliteSchemaService.EnsureSchema(_db, createIndexes: true);

        // Second call should be idempotent
        var act = () => SqliteSchemaService.EnsureSchema(_db, createIndexes: true);
        act.Should().NotThrow();
    }

    private List<string> GetTableNames()
    {
        var tables = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private List<string> GetColumnNames(string tableName)
    {
        var columns = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private List<string> GetIndexNames()
    {
        var indexes = new List<string>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            indexes.Add(reader.GetString(0));
        return indexes;
    }
}

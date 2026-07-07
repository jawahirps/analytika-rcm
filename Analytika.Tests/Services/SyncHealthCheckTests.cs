using Analytika.Models;
using Analytika.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Analytika.Tests.Services;

public class SyncHealthCheckTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public SyncHealthCheckTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:SyncStaleAfterHours"] = "26"
            })
            .Build();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CheckHealthAsync_NoActiveCredentials_ReturnsHealthy()
    {
        // Arrange — no credentials in the DB at all
        var sut = new SyncHealthCheck(_db, _config);

        // Act
        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No active portal credentials");
    }

    [Fact]
    public async Task CheckHealthAsync_AllFacilitiesSyncedRecently_ReturnsHealthy()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Clinic A", IsActive = true };
        _db.Facilities.Add(facility);

        _db.PortalCredentials.Add(new PortalCredential
        {
            Id = 1, Portal = "DHA", FacilityId = 1, Username = "user",
            PasswordEncrypted = "enc", IsActive = true
        });

        // Recent successful fetch log (2 hours ago)
        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Id = 1, Portal = "DHA", FacilityId = 1, Operation = "Sync",
            Status = "Success", FetchedAt = DateTime.UtcNow.AddHours(-2), FetchedBy = "system"
        });

        await _db.SaveChangesAsync();

        var sut = new SyncHealthCheck(_db, _config);

        // Act
        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("synced within");
    }

    [Fact]
    public async Task CheckHealthAsync_StaleFacility_ReturnsDegraded()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Stale Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        _db.PortalCredentials.Add(new PortalCredential
        {
            Id = 1, Portal = "DHA", FacilityId = 1, Username = "user",
            PasswordEncrypted = "enc", IsActive = true
        });

        // Stale fetch log (48 hours ago, past the 26h threshold)
        _db.PortalFetchLogs.Add(new PortalFetchLog
        {
            Id = 1, Portal = "DHA", FacilityId = 1, Operation = "Sync",
            Status = "Success", FetchedAt = DateTime.UtcNow.AddHours(-48), FetchedBy = "system"
        });

        await _db.SaveChangesAsync();

        var sut = new SyncHealthCheck(_db, _config);

        // Act
        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("degraded");
    }

    [Fact]
    public async Task CheckHealthAsync_FacilityNeverSynced_ReturnsDegraded()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "New Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        _db.PortalCredentials.Add(new PortalCredential
        {
            Id = 1, Portal = "DHA", FacilityId = 1, Username = "user",
            PasswordEncrypted = "enc", IsActive = true
        });

        // No fetch logs at all
        await _db.SaveChangesAsync();

        var sut = new SyncHealthCheck(_db, _config);

        // Act
        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("never synced");
    }

    [Fact]
    public async Task CheckHealthAsync_MixedFacilities_ReturnsDegradedWithCount()
    {
        // Arrange — one fresh, one stale
        _db.Facilities.AddRange(
            new Facility { Id = 1, Name = "Fresh Clinic", IsActive = true },
            new Facility { Id = 2, Name = "Stale Clinic", IsActive = true }
        );

        _db.PortalCredentials.AddRange(
            new PortalCredential
            {
                Id = 1, Portal = "DHA", FacilityId = 1, Username = "u1",
                PasswordEncrypted = "enc", IsActive = true
            },
            new PortalCredential
            {
                Id = 2, Portal = "DHA", FacilityId = 2, Username = "u2",
                PasswordEncrypted = "enc", IsActive = true
            }
        );

        _db.PortalFetchLogs.AddRange(
            new PortalFetchLog
            {
                Id = 1, Portal = "DHA", FacilityId = 1, Operation = "Sync",
                Status = "Success", FetchedAt = DateTime.UtcNow.AddHours(-1), FetchedBy = "system"
            },
            new PortalFetchLog
            {
                Id = 2, Portal = "DHA", FacilityId = 2, Operation = "Sync",
                Status = "Success", FetchedAt = DateTime.UtcNow.AddHours(-48), FetchedBy = "system"
            }
        );

        await _db.SaveChangesAsync();

        var sut = new SyncHealthCheck(_db, _config);

        // Act
        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("1/2 facilities degraded");
    }

    [Fact]
    public async Task CheckHealthAsync_InactiveCredentials_AreIgnored()
    {
        // Arrange — only inactive credential
        var facility = new Facility { Id = 1, Name = "Deactivated Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        _db.PortalCredentials.Add(new PortalCredential
        {
            Id = 1, Portal = "DHA", FacilityId = 1, Username = "user",
            PasswordEncrypted = "enc", IsActive = false
        });

        await _db.SaveChangesAsync();

        var sut = new SyncHealthCheck(_db, _config);

        // Act
        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        // Assert — no active credentials means healthy
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No active portal credentials");
    }
}

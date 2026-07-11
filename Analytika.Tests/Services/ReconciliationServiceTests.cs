using Analytika.Models;
using Analytika.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Analytika.Tests.Services;

public class ReconciliationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ReconciliationService _sut;

    public ReconciliationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new ReconciliationService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetReconciliationAsync_MatchesSubmissionToRemittanceByClaimId()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Test Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        var tx1 = new PortalTransaction
        {
            Id = 1, Portal = "DHA", FacilityId = 1, TransactionId = "TX001",
            Type = "Claim", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        var tx2 = new PortalTransaction
        {
            Id = 2, Portal = "DHA", FacilityId = 1, TransactionId = "TX002",
            Type = "Remittance", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        _db.PortalTransactions.AddRange(tx1, tx2);

        _db.XmlParsedRecords.AddRange(
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Submission",
                ClaimId = "CLM-001", GrossAmount = 1000m, NetAmount = 1000m,
                ReadyForReport = true, ParsedAt = DateTime.UtcNow
            },
            new XmlParsedRecord
            {
                PortalTransactionId = 2, FacilityId = 1, RecordKind = "Remittance",
                ClaimId = "CLM-001", PaidAmount = 1000m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Act
        var result = await _sut.GetReconciliationAsync(
            facilityIds: new List<int> { 1 }, dateFrom: null, dateTo: null, statusFilters: null);

        // Assert
        result.Rows.Should().HaveCount(1);
        result.Rows[0].ClaimId.Should().Be("CLM-001");
        result.Rows[0].PaymentStatus.Should().Be("Paid");
        result.Rows[0].PaidAmount.Should().Be(1000m);
    }

    [Fact]
    public async Task GetReconciliationAsync_UnmatchedSubmissions_StatusIsPending()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Test Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        var tx = new PortalTransaction
        {
            Id = 1, Portal = "DHA", FacilityId = 1, TransactionId = "TX001",
            Type = "Claim", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        _db.PortalTransactions.Add(tx);

        _db.XmlParsedRecords.AddRange(
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Submission",
                ClaimId = "CLM-100", GrossAmount = 500m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            },
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Submission",
                ClaimId = "CLM-101", GrossAmount = 300m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Act
        var result = await _sut.GetReconciliationAsync(
            facilityIds: new List<int> { 1 }, dateFrom: null, dateTo: null, statusFilters: null);

        // Assert — both claims have no remittance match
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().AllSatisfy(r => r.PaymentStatus.Should().Be("Pending"));
        result.PendingCount.Should().Be(2);
    }

    [Fact]
    public async Task GetReconciliationAsync_UnmatchedRemittances_HasPaidOrRejectedStatus()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Test Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        var tx = new PortalTransaction
        {
            Id = 1, Portal = "DHA", FacilityId = 1, TransactionId = "TX001",
            Type = "Remittance", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        _db.PortalTransactions.Add(tx);

        _db.XmlParsedRecords.AddRange(
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Remittance",
                ClaimId = "CLM-200", PaidAmount = 750m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            },
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Remittance",
                ClaimId = "CLM-201", PaidAmount = 0m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Act
        var result = await _sut.GetReconciliationAsync(
            facilityIds: new List<int> { 1 }, dateFrom: null, dateTo: null, statusFilters: null);

        // Assert — remittance-only entries: paid one is "Paid", zero-paid is "Rejected"
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().Contain(r => r.ClaimId == "CLM-200" && r.PaymentStatus == "Paid");
        result.Rows.Should().Contain(r => r.ClaimId == "CLM-201" && r.PaymentStatus == "Rejected");
    }

    [Fact]
    public async Task GetReconciliationAsync_PartialPayment_StatusIsPartial()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Test Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        var tx1 = new PortalTransaction
        {
            Id = 1, Portal = "DHA", FacilityId = 1, TransactionId = "TX001",
            Type = "Claim", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        var tx2 = new PortalTransaction
        {
            Id = 2, Portal = "DHA", FacilityId = 1, TransactionId = "TX002",
            Type = "Remittance", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        _db.PortalTransactions.AddRange(tx1, tx2);

        _db.XmlParsedRecords.AddRange(
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Submission",
                ClaimId = "CLM-300", GrossAmount = 1000m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            },
            new XmlParsedRecord
            {
                PortalTransactionId = 2, FacilityId = 1, RecordKind = "Remittance",
                ClaimId = "CLM-300", PaidAmount = 500m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Act
        var result = await _sut.GetReconciliationAsync(
            facilityIds: new List<int> { 1 }, dateFrom: null, dateTo: null, statusFilters: null);

        // Assert
        result.Rows.Should().ContainSingle();
        result.Rows[0].PaymentStatus.Should().Be("Partial");
        result.Rows[0].SubmittedAmount.Should().Be(1000m);
        result.Rows[0].PaidAmount.Should().Be(500m);
    }

    [Fact]
    public async Task GetReconciliationAsync_StatusFilter_FiltersResults()
    {
        // Arrange
        var facility = new Facility { Id = 1, Name = "Test Clinic", IsActive = true };
        _db.Facilities.Add(facility);

        var tx1 = new PortalTransaction
        {
            Id = 1, Portal = "DHA", FacilityId = 1, TransactionId = "TX001",
            Type = "Claim", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        var tx2 = new PortalTransaction
        {
            Id = 2, Portal = "DHA", FacilityId = 1, TransactionId = "TX002",
            Type = "Remittance", Status = "Active", Operation = "Sync", SyncedAt = DateTime.UtcNow
        };
        _db.PortalTransactions.AddRange(tx1, tx2);

        _db.XmlParsedRecords.AddRange(
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Submission",
                ClaimId = "CLM-400", GrossAmount = 500m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            },
            new XmlParsedRecord
            {
                PortalTransactionId = 2, FacilityId = 1, RecordKind = "Remittance",
                ClaimId = "CLM-400", PaidAmount = 500m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            },
            new XmlParsedRecord
            {
                PortalTransactionId = 1, FacilityId = 1, RecordKind = "Submission",
                ClaimId = "CLM-401", GrossAmount = 200m, ReadyForReport = true,
                ParsedAt = DateTime.UtcNow
            }
        );
        await _db.SaveChangesAsync();

        // Act — filter to only "Pending" status
        var result = await _sut.GetReconciliationAsync(
            facilityIds: new List<int> { 1 }, dateFrom: null, dateTo: null,
            statusFilters: new List<string> { "Pending" });

        // Assert — only the unmatched submission should remain
        result.Rows.Should().ContainSingle();
        result.Rows[0].ClaimId.Should().Be("CLM-401");
        result.Rows[0].PaymentStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task GetReconciliationAsync_EmptyDb_ReturnsEmptyRows()
    {
        // Arrange — add a facility but no parsed records
        _db.Facilities.Add(new Facility { Id = 1, Name = "Empty Clinic", IsActive = true });
        await _db.SaveChangesAsync();

        // Act
        var result = await _sut.GetReconciliationAsync(
            facilityIds: null, dateFrom: null, dateTo: null, statusFilters: null);

        // Assert
        result.Rows.Should().BeEmpty();
        result.TotalRowCount.Should().Be(0);
    }
}

using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Analytika.Models;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ReportRequest> ReportRequests { get; set; }
    public DbSet<Facility> Facilities { get; set; }
    public DbSet<Receiver> Receivers { get; set; }
    public DbSet<Payer> Payers { get; set; }
    public DbSet<Clinician> Clinicians { get; set; }
    public DbSet<Department> Departments { get; set; }
    public DbSet<UserFacility> UserFacilities { get; set; }
    public DbSet<PortalCredential> PortalCredentials { get; set; }
    public DbSet<UserReportAccess> UserReportAccesses { get; set; }
    public DbSet<PortalFetchLog> PortalFetchLogs { get; set; }
    public DbSet<PortalTransaction> PortalTransactions { get; set; }
    public DbSet<DhpoCodingSet> DhpoCodingSets { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<ReportSchedule> ReportSchedules { get; set; }
    public DbSet<RemittanceClaim> RemittanceClaims { get; set; }
    public DbSet<XmlParsedRecord> XmlParsedRecords { get; set; }
    public DbSet<ResubmissionTask> ResubmissionTasks { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ReportRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ReportId).HasMaxLength(20);
            entity.Property(e => e.ReportType).HasMaxLength(100);
            entity.Property(e => e.Status).HasMaxLength(50);
            entity.Property(e => e.FileFormat).HasMaxLength(20);
        });

        builder.Entity<UserFacility>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.User).WithMany(u => u.UserFacilities).HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserReportAccess>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.User).WithMany(u => u.ReportAccesses).HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortalCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortalFetchLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.FetchedAt);  // ORDER BY FetchedAt DESC is used in many queries
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortalTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Portal, e.FacilityId, e.TransactionId }).IsUnique();
            entity.HasIndex(e => new { e.FacilityId, e.FileDownloaded });  // PendingDownloadService WHERE FileDownloaded = false
            entity.HasIndex(e => new { e.FacilityId, e.FileId });         // Fetch page DB cross-reference by (FacilityId, FileId)
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SystemSetting>(entity =>
        {
            entity.HasIndex(e => new { e.Category, e.Key }).IsUnique();  // queried by Category + Key in email settings
        });

        builder.Entity<DhpoCodingSet>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Category, e.Code }).IsUnique();
        });

        builder.Entity<RemittanceClaim>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClaimId);
            entity.HasIndex(e => e.FacilityId);
            entity.HasIndex(e => e.RemittanceTransactionId);
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.RemittanceTransaction).WithMany().HasForeignKey(e => e.RemittanceTransactionId).OnDelete(DeleteBehavior.Cascade);
            entity.Ignore(e => e.DeniedAmount);
            entity.Ignore(e => e.IsFullyDenied);
            entity.Ignore(e => e.IsPartiallyPaid);
        });

        builder.Entity<XmlParsedRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PortalTransactionId);
            entity.HasIndex(e => new { e.FacilityId, e.RecordKind });
            entity.HasIndex(e => e.ClaimId);
            entity.HasIndex(e => e.ReadyForReport);
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.PortalTransaction).WithMany().HasForeignKey(e => e.PortalTransactionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ResubmissionTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.AssignedToUserId);
            entity.HasOne(e => e.RemittanceClaim).WithOne(c => c.Task).HasForeignKey<ResubmissionTask>(e => e.RemittanceClaimId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.AssignedTo).WithMany().HasForeignKey(e => e.AssignedToUserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(e => e.AssignedBy).WithMany().HasForeignKey(e => e.AssignedByUserId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}

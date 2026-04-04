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
    public DbSet<DashboardEmbed> DashboardEmbeds { get; set; }
    public DbSet<UserFacility> UserFacilities { get; set; }
    public DbSet<PortalCredential> PortalCredentials { get; set; }
    public DbSet<UserReportAccess> UserReportAccesses { get; set; }
    public DbSet<PortalFetchLog> PortalFetchLogs { get; set; }
    public DbSet<PortalTransaction> PortalTransactions { get; set; }
    public DbSet<DhpoCodingSet> DhpoCodingSets { get; set; }

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

        builder.Entity<DashboardEmbed>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TabName).HasMaxLength(50);
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
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PortalTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Portal, e.FacilityId, e.TransactionId }).IsUnique();
            entity.HasOne(e => e.Facility).WithMany().HasForeignKey(e => e.FacilityId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DhpoCodingSet>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Category, e.Code }).IsUnique();
        });
    }
}

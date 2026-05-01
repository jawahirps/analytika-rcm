using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Analytika.Services;

namespace Analytika.Models;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var context = new AppDbContext(
            serviceProvider.GetRequiredService<DbContextOptions<AppDbContext>>());

        // Seed facilities
        if (!context.Facilities.Any())
        {
            context.Facilities.AddRange(
                new Facility { Name = "Alnoor Deira" },
                new Facility { Name = "Alnoor Rashidiya" },
                new Facility { Name = "Alnoor Abu Dhabi" }
            );
        }

        if (!context.Receivers.Any())
        {
            context.Receivers.AddRange(
                new Receiver { Name = "Neuron LLC - Dha" },
                new Receiver { Name = "Mednet UAE" },
                new Receiver { Name = "Emirates Insurance" }
            );
        }

        if (!context.Payers.Any())
        {
            context.Payers.AddRange(
                new Payer { Name = "Dewa - Dha" },
                new Payer { Name = "Dubai Health Authority" },
                new Payer { Name = "ADNIC" },
                new Payer { Name = "AXA Gulf" }
            );
        }

        if (!context.Clinicians.Any())
        {
            context.Clinicians.AddRange(
                new Clinician { Name = "Dr. Ahmed Al Mansoori" },
                new Clinician { Name = "Dr. Sara Hassan" },
                new Clinician { Name = "Dr. Mohammed Al Rashidi" }
            );
        }

        if (!context.Departments.Any())
        {
            context.Departments.AddRange(
                new Department { Name = "Emergency" },
                new Department { Name = "Cardiology" },
                new Department { Name = "Orthopedics" },
                new Department { Name = "Radiology" }
            );
        }

        if (!context.DashboardEmbeds.Any())
        {
            foreach (var dashboard in HardcodedDashboardCatalog.Dashboards)
            {
                context.DashboardEmbeds.Add(new DashboardEmbed
                {
                    TabName = dashboard.TabName,
                    ReportId = dashboard.ReportId,
                    GroupId = dashboard.GroupId,
                    EmbedToken = "PENDING",
                    EmbedUrl = dashboard.EmbedUrl,
                    TokenExpiry = DateTime.UtcNow.AddHours(1),
                    IsActive = true
                });
            }
        }

        await context.SaveChangesAsync();

        // Seed admin user
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in new[] { "Admin", "FacilityAdmin", "Analyst", "Viewer" })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        if (await userManager.FindByEmailAsync("admin@ghafbi.ae") == null)
        {
            var admin = new ApplicationUser
            {
                UserName = "admin@ghafbi.ae",
                Email = "admin@ghafbi.ae",
                FullName = "System Administrator",
                EmailConfirmed = true
            };
            await userManager.CreateAsync(admin, "Admin@123");
            await userManager.AddToRoleAsync(admin, "Admin");
        }

        // Seed demo report requests
        if (!context.ReportRequests.Any())
        {
            var random = new Random(42);
            var statuses = new[] { "Completed", "Pending", "Processing", "Failed" };
            var reportTypes = new[] { "ClaimSummary", "ClaimActivity", "RemittanceActivity", "ClaimReceiver", "ClaimClinician", "FinanceTAT", "DenialReport", "ClaimLifeCycle" };

            for (int i = 1; i <= 50; i++)
            {
                var from = new DateTime(2026, 1, 1).AddDays(random.Next(0, 60));
                context.ReportRequests.Add(new ReportRequest
                {
                    ReportId = $"ANA-{3000000 + i:D7}",
                    ReportType = reportTypes[random.Next(reportTypes.Length)],
                    BranchId = random.Next(1, 4),
                    ReceiverId = random.Next(1, 4),
                    PayerId = random.Next(1, 5),
                    ClinicianId = random.Next(1, 4),
                    DepartmentId = random.Next(1, 5),
                    DateFrom = from,
                    DateTo = from.AddDays(random.Next(7, 60)),
                    Status = statuses[random.Next(statuses.Length)],
                    RequestedAt = from.AddDays(-1),
                    GeneratedAt = DateTime.UtcNow.AddDays(-random.Next(1, 30)),
                    FileFormat = "Excel",
                    RequestedBy = "admin@ghafbi.ae"
                });
            }
            await context.SaveChangesAsync();
        }
    }
}

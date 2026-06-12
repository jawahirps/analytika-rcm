using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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

        await context.SaveChangesAsync();

        // Seed admin user
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in new[] { "Admin", "FacilityAdmin", "Analyst", "Billing", "Finance", "Auditor", "Viewer", "Reporter" })
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

        // Facility Reporter accounts — one per facility, reports + support access only
        var facilityAccounts = new[]
        {
            (Email: "alnoor.deira@ghafbi.ae",        Name: "Alnoor Deira"),
            (Email: "alnoor.rashidiya@ghafbi.ae",     Name: "Alnoor Rashidiya"),
            (Email: "alnoor.abudhabi@ghafbi.ae",      Name: "Alnoor Abu Dhabi"),
            (Email: "alnoor.muhaisnah@ghafbi.ae",     Name: "Alnoor Muhaisnah"),
            (Email: "alnoor.qusais@ghafbi.ae",        Name: "Alnoor Qusais"),
            (Email: "apollo@ghafbi.ae",               Name: "Apollo"),
            (Email: "axon.rashidiya@ghafbi.ae",       Name: "Axon -Rashidiya"),
            (Email: "marinca.mc@ghafbi.ae",           Name: "Marinca MC"),
            (Email: "nafeesa.mc@ghafbi.ae",           Name: "Nafeesa MC"),
            (Email: "newlotus.mc@ghafbi.ae",          Name: "New Lotus MC"),
            (Email: "sabah.alnoor.altaif@ghafbi.ae",  Name: "Sabah Alnoor Al Taif"),
            (Email: "sabah.alnoor.mc@ghafbi.ae",      Name: "Sabah Alnoor MC"),
            (Email: "sanlucas.mc@ghafbi.ae",          Name: "San Lucas MC"),
            (Email: "wecare@ghafbi.ae",               Name: "wecare"),
        };

        foreach (var (email, facilityName) in facilityAccounts)
        {
            if (await userManager.FindByEmailAsync(email) != null) continue;

            var facility = await context.Facilities
                .FirstOrDefaultAsync(f => f.Name == facilityName);
            if (facility == null) continue;

            var reporter = new ApplicationUser
            {
                UserName = email,
                Email = email,
                FullName = facilityName,
                EmailConfirmed = true,
                UserType = "Facility"
            };
            var result = await userManager.CreateAsync(reporter, "ghafbi@1234");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(reporter, "Reporter");
                context.Set<UserFacility>().Add(new UserFacility
                {
                    UserId = reporter.Id,
                    FacilityId = facility.Id
                });
            }
        }
        await context.SaveChangesAsync();

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

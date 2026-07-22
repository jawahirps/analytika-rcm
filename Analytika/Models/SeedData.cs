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

        // Intentionally no dummy Receivers/Payers/Clinicians/Departments seeds.
        // These are synced from real XmlParsedRecords data below.

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
                EmailConfirmed = false,   // requires admin to confirm / reset before first login
                LockoutEnabled = true,
                LockoutEnd = DateTimeOffset.MaxValue,
                UserType = "Facility"
            };
            // Random one-time password — account is locked, so it cannot be used until admin resets it.
            var result = await userManager.CreateAsync(reporter, Guid.NewGuid().ToString("N") + "Aa1!");
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

        // Sync Payers/Receivers/Clinicians from real XmlParsedRecords codes.
        // Runs once: detects stale dummy data and replaces it; no-ops on clean DB.
        if (await context.XmlParsedRecords.AnyAsync())
        {
            // Only replace data if known legacy dummy rows are present — never wipe operator-managed data.
            bool stale =
                await context.Payers.AnyAsync(p => p.Name == "Dewa - Dha" || p.Name == "Dubai Health Authority") ||
                await context.Receivers.AnyAsync(r => r.Name == "Neuron LLC - Dha" || r.Name == "Mednet UAE") ||
                await context.Clinicians.AnyAsync(c => c.Name == "Dr. Ahmed Al Mansoori");

            if (stale)
            {
                // NULL FK columns in ReportRequests first to avoid FK constraint violations
                await context.ReportRequests.ExecuteUpdateAsync(r => r
                    .SetProperty(x => x.PayerId,      (int?)null)
                    .SetProperty(x => x.ReceiverId,   (int?)null)
                    .SetProperty(x => x.ClinicianId,  (int?)null)
                    .SetProperty(x => x.DepartmentId, (int?)null));

                await context.Payers.ExecuteDeleteAsync();
                await context.Receivers.ExecuteDeleteAsync();
                await context.Clinicians.ExecuteDeleteAsync();
                await context.Departments.ExecuteDeleteAsync();

                var payerCodes = await context.XmlParsedRecords
                    .Where(r => r.PayerId != null && r.PayerId != "")
                    .Select(r => r.PayerId!).Distinct().OrderBy(x => x).ToListAsync();
                context.Payers.AddRange(payerCodes.Select(c => new Payer { Name = c }));

                var receiverCodes = await context.XmlParsedRecords
                    .Where(r => r.ReceiverId != null && r.ReceiverId != "")
                    .Select(r => r.ReceiverId!).Distinct().OrderBy(x => x).ToListAsync();
                context.Receivers.AddRange(receiverCodes.Select(c => new Receiver { Name = c }));

                var clinicianCodes = await context.XmlParsedRecords
                    .Where(r => r.Clinician != null && r.Clinician != "")
                    .Select(r => r.Clinician!).Distinct().OrderBy(x => x).ToListAsync();
                context.Clinicians.AddRange(clinicianCodes.Select(c => new Clinician { Name = c }));

                await context.SaveChangesAsync();
            }
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
                    BranchId = null,      // omit FK ids — lookup rows may not exist on fresh databases
                    ReceiverId = null,
                    PayerId = null,
                    ClinicianId = null,
                    DepartmentId = null,
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

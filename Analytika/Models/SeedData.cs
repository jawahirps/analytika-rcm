using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Analytika.Models;

public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var context = new AppDbContext(
            serviceProvider.GetRequiredService<DbContextOptions<AppDbContext>>());

        // Seed facilities
        var requiredFacilities = new[]
        {
            "Alnoor Deira",
            "Alnoor Rashidiya",
            "Alnoor Abu Dhabi",
            "Noor Al Shifa Medical Center Ajman",
            "Noor Al Shifa Medical Center UAQ"
        };

        foreach (var facilityName in requiredFacilities)
        {
            if (!await context.Facilities.AnyAsync(f => f.Name == facilityName))
            {
                context.Facilities.Add(new Facility { Name = facilityName, IsActive = true });
            }
        }

        await context.SaveChangesAsync();

        await UpsertPortalCredentialAsync(
            context,
            "Noor Al Shifa Medical Center Ajman",
            "DHA",
            "Noor Al Shifa Ajman eClaim",
            "NOOR AL SHIFA",
            "NOOR@1232024",
            "https://dhpo.eclaimlink.ae",
            null);

        await UpsertPortalCredentialAsync(
            context,
            "Noor Al Shifa Medical Center Ajman",
            "RHA",
            "Noor Al Shifa Ajman RHA",
            "info@nooralshifa.ae",
            "NOORalshifa@2026",
            "https://tmbapi.riayati.ae:8083",
            "c03ab47d-afeb-4dfa-a048-856338db3764");

        await UpsertPortalCredentialAsync(
            context,
            "Noor Al Shifa Medical Center UAQ",
            "DHA",
            "Noor Al Shifa UAQ eClaim",
            "NOOR AL SHIFA UAQ",
            "Noor@2025",
            "https://dhpo.eclaimlink.ae",
            null);

        await UpsertPortalCredentialAsync(
            context,
            "Noor Al Shifa Medical Center UAQ",
            "RHA",
            "Noor Al Shifa UAQ RHA",
            "infouaq@nooralshifa.ae",
            "Noor@0542321553",
            "https://tmbapi.riayati.ae:8083",
            "385a3b2b-5168-4df4-ae8d-edc9559f1987");

        // Seed admin user
        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in new[] { "Admin", "FacilityAdmin", "Analyst", "Billing", "Finance", "Auditor", "Viewer" })
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

    }

    private static async Task UpsertPortalCredentialAsync(
        AppDbContext context,
        string facilityName,
        string portal,
        string credentialName,
        string username,
        string password,
        string apiBaseUrl,
        string? licenseCode)
    {
        var facility = await context.Facilities.FirstAsync(f => f.Name == facilityName);
        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var existing = await context.PortalCredentials
            .FirstOrDefaultAsync(c => c.FacilityId == facility.Id && c.Portal == portal);

        if (existing == null)
        {
            context.PortalCredentials.Add(new PortalCredential
            {
                Portal = portal,
                FacilityId = facility.Id,
                CredentialName = credentialName,
                Username = username,
                PasswordEncrypted = encodedPassword,
                ApiBaseUrl = apiBaseUrl,
                LicenseCode = licenseCode,
                IsActive = true
            });
            await context.SaveChangesAsync();
            return;
        }

        existing.CredentialName = credentialName;
        existing.Username = username;
        existing.PasswordEncrypted = encodedPassword;
        existing.ApiBaseUrl = apiBaseUrl;
        existing.LicenseCode = licenseCode;
        existing.IsActive = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }
}

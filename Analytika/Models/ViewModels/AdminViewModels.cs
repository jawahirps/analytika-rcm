using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace Analytika.Models.ViewModels;

public class UserListViewModel
{
    public List<UserRowViewModel> Users { get; set; } = new();
    public int TotalUsers { get; set; }
    public string Filter { get; set; } = "All"; // All, Global, Facility, Active, Inactive
}

public class UserRowViewModel
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string UserType { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> Facilities { get; set; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateUserViewModel
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
    public string Role { get; set; } = "Analyst";
    public string UserType { get; set; } = "Global";
    public List<int> SelectedFacilityIds { get; set; } = new();
    public List<SelectListItem> AvailableRoles { get; set; } = new();
    public List<SelectListItem> AvailableFacilities { get; set; } = new();
    public List<string> AllDashboardTabs { get; set; } = new();
    public List<string> AllReportTypes { get; set; } = new();
    public List<string> SelectedDashboards { get; set; } = new();
    public List<string> SelectedReports { get; set; } = new();
}

public class EditUserViewModel
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = "Analyst";
    public string UserType { get; set; } = "Global";
    public bool IsActive { get; set; }
    public List<int> SelectedFacilityIds { get; set; } = new();
    public List<SelectListItem> AvailableRoles { get; set; } = new();
    public List<SelectListItem> AvailableFacilities { get; set; } = new();
    public List<string> AllDashboardTabs { get; set; } = new();
    public List<string> AllReportTypes { get; set; } = new();
    public List<string> SelectedDashboards { get; set; } = new();
    public List<string> SelectedReports { get; set; } = new();
}

public class UserProfileViewModel
{
    [Required, MaxLength(120)]
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    [MaxLength(120)]
    public string? Department { get; set; }
    [Phone, MaxLength(32)]
    public string? PhoneNumber { get; set; }
    public string UserType { get; set; } = "Global";
    public bool IsActive { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<string> Facilities { get; set; } = new();
    public List<string> DashboardAccess { get; set; } = new();
    public List<string> ReportAccess { get; set; } = new();
}

public class RoleListViewModel
{
    public List<RoleRowViewModel> Roles { get; set; } = new();
    public string NewRoleName { get; set; } = string.Empty;
    public List<string> StandardRoles { get; set; } = new();
}

public class RoleRowViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int UserCount { get; set; }
    public bool IsProtected { get; set; }
    public bool IsStandard { get; set; }
}

public class CredentialListViewModel
{
    public List<PortalCredentialViewModel> Credentials { get; set; } = new();
    public List<SelectListItem> Facilities { get; set; } = new();
}

public class PortalCredentialViewModel
{
    public int Id { get; set; }
    public string Portal { get; set; } = string.Empty;
    public string FacilityName { get; set; } = string.Empty;
    public int FacilityId { get; set; }
    public string? NewFacilityName { get; set; }  // set when user types a brand-new facility
    public string? CredentialName { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // plain text for form, encrypted on save
    public string? ApiBaseUrl { get; set; }
    public string? LicenseCode { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? UpdatedAt { get; set; }
}

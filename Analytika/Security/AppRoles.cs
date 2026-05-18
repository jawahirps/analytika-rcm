namespace Analytika.Security;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string FacilityAdmin = "FacilityAdmin";
    public const string Analyst = "Analyst";
    public const string Billing = "Billing";
    public const string Finance = "Finance";
    public const string Auditor = "Auditor";
    public const string Viewer = "Viewer";

    public const string RcmAccess = $"{Admin},{FacilityAdmin},{Analyst},{Billing},{Finance},{Auditor}";
    public const string ReportAccess = RcmAccess;
    public const string ResubmissionAccess = $"{Admin},{FacilityAdmin},{Analyst}";
    public const string AdminAccess = Admin;
}

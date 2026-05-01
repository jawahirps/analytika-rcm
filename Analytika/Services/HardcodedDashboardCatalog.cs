using Analytika.Models;

namespace Analytika.Services;

public static class HardcodedDashboardCatalog
{
    public static readonly IReadOnlyList<HardcodedDashboard> Dashboards =
    [
        new("Submissions", "103a341b-ad73-41b9-b933-72695f2f9cb9", "4cca8060-254a-4256-ab07-90dde8be4ab5"),
        new("Resubmissions", "fe086d8c-77f6-4be3-98ca-c673ddbded4d", "4bb49882-723b-47a5-84ac-f0cde707e8b0"),
        new("Remittance", "55d984ee-19b3-4e34-aa3a-056b7152c4d2", "c616c2a6-fbb7-4998-854e-6ca6956be626"),
        new("Denials", "a419f88c-7541-44b3-8825-78ee1c75f330", "8054547e-48f4-4fd4-b2fa-f3707ca99c01"),
        new("Clinicians", "fddc2ad8-4f9a-49b1-a347-ddb3dcfee73d", "14c65482-ae5c-4e4d-80ed-8c2578085ff8"),
        new("Operations", "736d62a5-cd27-42f6-b76f-696dec6efda9", "8d74c0fc-6fe4-49a9-b797-23ab7efeb136"),
        new("Insurance", "fa613e0c-3dd3-42e5-9ded-8ce22b9ba7a1", "648c307b-eaaa-45c8-b0c4-92185c54924a"),
        new("Department", "dd520eb2-db8e-42f5-b021-85b2c9e266a0", "412c6e78-b1ea-4627-9f24-d6d1b90c2dcf")
    ];

    public static HardcodedDashboard? Find(string tabName) =>
        Dashboards.FirstOrDefault(d => d.TabName.Equals(tabName, StringComparison.OrdinalIgnoreCase));

    public static List<DashboardEmbed> ToDashboardEmbeds() =>
        Dashboards.Select((dashboard, index) => new DashboardEmbed
        {
            Id = index + 1,
            TabName = dashboard.TabName,
            GroupId = dashboard.GroupId,
            ReportId = dashboard.ReportId,
            EmbedToken = "HARDCODED",
            EmbedUrl = dashboard.EmbedUrl,
            TokenExpiry = DateTime.UtcNow.AddHours(1),
            IsActive = true
        }).ToList();
}

public sealed record HardcodedDashboard(string TabName, string GroupId, string ReportId)
{
    public string EmbedUrl => $"https://app.powerbi.com/reportEmbed?reportId={ReportId}&groupId={GroupId}";
}

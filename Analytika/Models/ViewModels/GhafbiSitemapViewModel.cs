namespace Analytika.Models.ViewModels;

public sealed record GhafbiSitemapViewModel(
    IReadOnlyList<SitemapSection> Sections,
    IReadOnlyList<string> PrimaryNavigation,
    IReadOnlyList<string> MobileNavigation,
    IReadOnlyList<UserFlow> UserFlows);

public sealed record SitemapSection(
    string Title,
    string Icon,
    IReadOnlyList<SitemapNode> Items);

public sealed record SitemapNode(
    string Title,
    IReadOnlyList<SitemapNode>? Children = null);

public sealed record UserFlow(
    string Title,
    string Icon,
    IReadOnlyList<string> Steps);

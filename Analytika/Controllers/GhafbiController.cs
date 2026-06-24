using Analytika.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Analytika.Controllers;

[Authorize]
public class GhafbiController : Controller
{
    [HttpGet]
    public IActionResult Sitemap()
    {
        return View(BuildSitemap());
    }

    [HttpGet]
    public IActionResult Workspace()
    {
        return View();
    }

    private static GhafbiSitemapViewModel BuildSitemap()
    {
        var sections = new List<SitemapSection>
        {
            Section("Public Website", "fa-globe",
                Node("Home"),
                Node("Product", Node("Features"), Node("Dashboards"), Node("Reports"), Node("Integrations"), Node("Security")),
                Node("Solutions", Node("Business Intelligence"), Node("Marketing Analytics"), Node("Sales Analytics"), Node("Operations Analytics"), Node("Executive Reporting")),
                Node("Pricing"),
                Node("Case Studies"),
                Node("Resources", Node("Blog"), Node("Guides"), Node("Documentation"), Node("FAQs")),
                Node("About"),
                Node("Contact"),
                Node("Login"),
                Node("Sign Up")),
            Section("Authentication", "fa-lock",
                Node("Login"), Node("Register"), Node("Forgot Password"), Node("Reset Password"), Node("Email Verification"), Node("Two-Factor Authentication")),
            Section("Onboarding", "fa-route",
                Node("Welcome"), Node("Create Workspace"), Node("Invite Team Members"), Node("Connect Data Source"), Node("Choose Dashboard Template"), Node("Setup Complete")),
            Section("App Dashboard", "fa-gauge-high",
                Node("Overview", Node("Key Metrics"), Node("Recent Activity"), Node("Data Health"), Node("Alerts Summary")),
                Node("Custom Dashboards", Node("All Dashboards"), Node("Create Dashboard"), Node("Dashboard Detail", Node("Widgets"), Node("Filters"), Node("Date Range"), Node("Share Dashboard"), Node("Export Dashboard")), Node("Dashboard Templates")),
                Node("Favorites")),
            Section("Analytics", "fa-chart-line",
                Node("Traffic Analytics", Node("Visitors"), Node("Sessions"), Node("Sources"), Node("Geography")),
                Node("Sales Analytics", Node("Revenue"), Node("Pipeline"), Node("Conversion Rate"), Node("Forecasting")),
                Node("Marketing Analytics", Node("Campaigns"), Node("Channels"), Node("Attribution"), Node("ROI")),
                Node("Customer Analytics", Node("Segments"), Node("Retention"), Node("Churn"), Node("Lifetime Value")),
                Node("Product Analytics", Node("Feature Usage"), Node("User Journeys"), Node("Events"), Node("Funnels")),
                Node("Financial Analytics", Node("Revenue"), Node("Expenses"), Node("Profitability"), Node("Cash Flow"))),
            Section("Reports", "fa-file-lines",
                Node("All Reports"), Node("Create Report"), Node("Scheduled Reports"), Node("Report Templates"), Node("Report Detail", Node("View Report"), Node("Edit Report"), Node("Share Report"), Node("Export PDF"), Node("Export CSV")), Node("Report Archive")),
            Section("Data", "fa-database",
                Node("Data Sources", Node("Connected Sources"), Node("Add Data Source"), Node("Source Settings"), Node("Sync History")),
                Node("Integrations", Node("Google Analytics"), Node("Meta Ads"), Node("Google Ads"), Node("Shopify"), Node("Stripe"), Node("HubSpot"), Node("Salesforce"), Node("Custom API")),
                Node("Data Explorer", Node("Tables"), Node("Queries"), Node("Filters"), Node("Saved Views")),
                Node("Data Cleaning", Node("Validation Rules"), Node("Duplicate Detection"), Node("Error Logs")),
                Node("Data Export")),
            Section("Alerts & Monitoring", "fa-bell",
                Node("Alerts Overview"), Node("Create Alert"), Node("Alert Rules"), Node("Triggered Alerts"), Node("Notification Settings"), Node("Alert History")),
            Section("AI Insights", "fa-wand-magic-sparkles",
                Node("Insights Feed"), Node("Anomaly Detection"), Node("Trend Analysis"), Node("Forecasts"), Node("Recommendations"), Node("Ask Ghafbi AI")),
            Section("Collaboration", "fa-comments",
                Node("Team Activity"), Node("Comments"), Node("Shared Dashboards"), Node("Shared Reports"), Node("Mentions")),
            Section("Workspace Settings", "fa-sliders",
                Node("Workspace Profile"), Node("Team Members", Node("Invite Members"), Node("Roles & Permissions"), Node("Access Logs")),
                Node("Billing", Node("Current Plan"), Node("Upgrade Plan"), Node("Payment Methods"), Node("Invoices")),
                Node("Branding", Node("Logo"), Node("Colors"), Node("White Label Settings")),
                Node("API Keys"), Node("Webhooks"), Node("Audit Logs")),
            Section("User Account", "fa-user",
                Node("Profile"), Node("Preferences"), Node("Password & Security"), Node("Notification Preferences"), Node("Connected Accounts"), Node("Logout")),
            Section("Help & Support", "fa-circle-question",
                Node("Help Center"), Node("Documentation"), Node("Tutorials"), Node("Contact Support"), Node("Submit Ticket"), Node("System Status"), Node("Feedback")),
            Section("Admin Panel", "fa-shield-halved",
                Node("Admin Overview"), Node("Users"), Node("Workspaces"), Node("Subscriptions"), Node("Integrations Management"), Node("Usage Analytics"), Node("Support Tickets"), Node("Content Management"), Node("Feature Flags"), Node("System Settings"))
        };

        var flows = new List<UserFlow>
        {
            Flow("New user flow", "fa-user-plus", "Sign Up", "Verify Email", "Create Workspace", "Connect Data Source", "Choose Template", "View Dashboard"),
            Flow("Analytics flow", "fa-chart-simple", "Login", "Overview", "Analytics", "Select Category", "Apply Filters", "Save View", "Export / Share"),
            Flow("Report flow", "fa-file-export", "Reports", "Create Report", "Select Data Source", "Choose Metrics", "Customize Layout", "Schedule / Export"),
            Flow("Integration flow", "fa-plug", "Data", "Integrations", "Select Platform", "Connect Account", "Map Fields", "Test Sync", "Activate"),
            Flow("Admin flow", "fa-user-shield", "Admin Panel", "Users / Workspaces / Subscriptions", "Review Usage", "Manage Access / Billing")
        };

        return new GhafbiSitemapViewModel(
            sections,
            new[] { "Overview", "Dashboards", "Analytics", "Reports", "Data", "AI Insights", "Alerts", "Settings" },
            new[] { "Home", "Dashboards", "Reports", "Insights", "More" },
            flows);
    }

    private static SitemapSection Section(string title, string icon, params SitemapNode[] items) => new(title, icon, items);

    private static SitemapNode Node(string title, params SitemapNode[] children) => new(title, children.Length == 0 ? null : children);

    private static UserFlow Flow(string title, string icon, params string[] steps) => new(title, icon, steps);
}

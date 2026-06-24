export const DASHBOARD_DATA = {
  Overview: {
    metrics: [
      { label: "Revenue", value: "AED 18.4M", change: "+12.8%", tone: "good" },
      { label: "Active dashboards", value: "42", change: "+6", tone: "good" },
      { label: "Data health", value: "97.6%", change: "3 issues", tone: "warn" },
      { label: "Open alerts", value: "8", change: "2 urgent", tone: "bad" },
    ],
    rows: [
      ["Revenue Forecast", "Sales Analytics", "Updated 12 min ago", "Ready"],
      ["Meta Ads Sync", "Marketing Data", "Running now", "Syncing"],
      ["Executive Pack", "Reports", "Scheduled 08:00", "Queued"],
      ["Churn Watchlist", "AI Insights", "Flagged today", "Review"],
    ],
  },
  Dashboards: {
    metrics: [
      { label: "Published", value: "42", change: "+6", tone: "good" },
      { label: "Shared views", value: "18", change: "+3", tone: "good" },
      { label: "Templates", value: "12", change: "4 new", tone: "info" },
      { label: "Favorites", value: "9", change: "+2", tone: "good" },
    ],
    rows: [
      ["Executive Overview", "Dashboard", "Viewed just now", "Live"],
      ["Operations Command", "Dashboard", "Updated 28 min ago", "Live"],
      ["Finance Performance", "Template", "Pinned today", "Ready"],
      ["Marketing Growth", "Dashboard", "Shared yesterday", "Review"],
    ],
  },
  Analytics: {
    metrics: [
      { label: "Visitors", value: "1.2M", change: "+8.1%", tone: "good" },
      { label: "Conversion", value: "6.7%", change: "+0.9%", tone: "good" },
      { label: "Forecast", value: "AED 21M", change: "+9.4%", tone: "good" },
      { label: "Churn risk", value: "3.8%", change: "-1.1%", tone: "good" },
    ],
    rows: [
      ["Traffic Sources", "Traffic", "Updated 4 min ago", "Ready"],
      ["Pipeline Forecast", "Sales", "Updated 16 min ago", "Ready"],
      ["Campaign ROI", "Marketing", "Updated 24 min ago", "Review"],
      ["Retention Cohorts", "Customer", "Updated 1h ago", "Ready"],
    ],
  },
  Reports: {
    metrics: [
      { label: "All reports", value: "128", change: "+11", tone: "good" },
      { label: "Scheduled", value: "24", change: "6 today", tone: "info" },
      { label: "Exports", value: "412", change: "+18%", tone: "good" },
      { label: "Archive", value: "1.8K", change: "clean", tone: "good" },
    ],
    rows: [
      ["Executive Pack", "Scheduled", "08:00 daily", "Queued"],
      ["Revenue Analysis", "PDF Export", "15 min ago", "Ready"],
      ["Claims Summary", "CSV Export", "42 min ago", "Ready"],
      ["Board Review", "Template", "Updated today", "Draft"],
    ],
  },
  Data: {
    metrics: [
      { label: "Sources", value: "18", change: "16 live", tone: "good" },
      { label: "Sync rate", value: "98.2%", change: "+1.6%", tone: "good" },
      { label: "Warnings", value: "3", change: "mapping", tone: "warn" },
      { label: "Saved views", value: "31", change: "+5", tone: "good" },
    ],
    rows: [
      ["Google Analytics", "Connected Source", "Live", "Healthy"],
      ["Salesforce", "Integration", "Live", "Healthy"],
      ["Stripe", "Integration", "Delayed", "Warning"],
      ["HubSpot", "Integration", "Live", "Healthy"],
    ],
  },
  "AI Insights": {
    metrics: [
      { label: "Insights", value: "36", change: "+9", tone: "good" },
      { label: "Anomalies", value: "5", change: "2 urgent", tone: "bad" },
      { label: "Forecasts", value: "14", change: "+4", tone: "good" },
      { label: "Actions taken", value: "22", change: "+31%", tone: "good" },
    ],
    rows: [
      ["Forecast lift", "Recommendation", "Just now", "Ready"],
      ["OR utilization dip", "Anomaly", "15 min ago", "Review"],
      ["Patient volume growth", "Trend", "1h ago", "Ready"],
      ["Campaign CPC drift", "Anomaly", "2h ago", "Watch"],
    ],
  },
  Alerts: {
    metrics: [
      { label: "Triggered", value: "8", change: "2 urgent", tone: "bad" },
      { label: "Rules", value: "34", change: "+2", tone: "good" },
      { label: "Resolved", value: "19", change: "7 days", tone: "good" },
      { label: "Recipients", value: "28", change: "+4", tone: "info" },
    ],
    rows: [
      ["Revenue sync", "Critical", "Open", "Urgent"],
      ["Claim backlog", "Critical", "Open", "Urgent"],
      ["Data freshness", "Warning", "Monitoring", "Warning"],
      ["Mapping drift", "Warning", "Monitoring", "Warning"],
    ],
  },
  Settings: {
    metrics: [
      { label: "Members", value: "86", change: "+8", tone: "good" },
      { label: "Roles", value: "12", change: "stable", tone: "good" },
      { label: "API keys", value: "7", change: "2 rotate", tone: "warn" },
      { label: "Audit events", value: "2.4K", change: "30 days", tone: "info" },
    ],
    rows: [
      ["Workspace Profile", "Settings", "Updated today", "Ready"],
      ["Roles & Permissions", "Access", "Reviewed 2h ago", "Ready"],
      ["API Keys", "Security", "Rotation due", "Review"],
      ["Audit Logs", "Compliance", "Live stream", "Ready"],
    ],
  },
};

export const DATA_SOURCES = [
  ["Google Analytics", "Live", 99],
  ["Salesforce", "Live", 96],
  ["Stripe", "Warning", 82],
  ["HubSpot", "Live", 94],
];

export const CHART_PATHS = {
  Revenue: "M40 190 C110 174 130 112 196 126 C270 142 286 86 360 96 C438 108 462 62 532 72 C610 84 634 44 724 54",
  Claims: "M40 174 C106 148 152 164 218 134 C284 104 326 124 392 96 C466 64 512 84 578 70 C646 54 686 72 724 46",
  Cost: "M40 96 C112 104 148 128 214 124 C280 120 318 154 386 148 C456 142 496 174 568 166 C636 158 676 184 724 176",
};

export const AI_INSIGHTS = [
  { tone: "good", title: "Forecast lift detected", text: "Revenue may close 9.4% above target if current collections hold." },
  { tone: "warn", title: "Stripe sync needs review", text: "Payment events are delayed by 41 minutes in two facilities." },
  { tone: "info", title: "Campaign anomaly", text: "Meta Ads CPC increased while conversion stayed flat." },
];

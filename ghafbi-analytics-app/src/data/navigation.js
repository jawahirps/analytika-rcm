import {
  Bell,
  Database,
  FileText,
  Gauge,
  LayoutDashboard,
  LineChart,
  Settings,
  Sparkles,
} from "lucide-react";

export const NAV_ITEMS = [
  { id: "Overview", label: "Overview", icon: Gauge, description: "Workspace command center and executive metrics." },
  { id: "Dashboards", label: "Dashboards", icon: LayoutDashboard, description: "Saved boards, templates, and shared analytics views." },
  { id: "Analytics", label: "Analytics", icon: LineChart, description: "Traffic, revenue, customer, product, and financial analysis." },
  { id: "Reports", label: "Reports", icon: FileText, description: "Scheduled packs, report builder, archive, and exports." },
  { id: "Data", label: "Data", icon: Database, description: "Sources, integrations, sync health, explorer, and cleaning rules." },
  { id: "AI Insights", label: "AI Insights", icon: Sparkles, description: "Forecasts, anomalies, trends, and AI recommendations." },
  { id: "Alerts", label: "Alerts", icon: Bell, description: "Rules, triggered alerts, notification settings, and history." },
  { id: "Settings", label: "Settings", icon: Settings, description: "Workspace, billing, branding, keys, webhooks, and audit logs." },
];

export const MOBILE_NAV = [
  { label: "Home", target: "Overview" },
  { label: "Dashboards", target: "Dashboards" },
  { label: "Reports", target: "Reports" },
  { label: "Insights", target: "AI Insights" },
  { label: "More", target: "Settings" },
];

export const PERIOD_OPTIONS = ["7D", "30D", "90D", "YTD"];

export const CHART_METRICS = ["Revenue", "Claims", "Cost"];

export const FILTER_CHIPS = ["Region: UAE", "Facility: All", "Channel: All", "Status: Live"];

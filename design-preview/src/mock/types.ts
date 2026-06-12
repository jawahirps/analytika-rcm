// ─────────────────────────────────────────────────────────────────────────
// SYNTHETIC PREVIEW DATA TYPES — NO PHI.
// Hand-mirrored from the production C# view models so the prototype is
// data-shape-faithful (Analytika/Models/ViewModels/*). Zero compile coupling.
// ─────────────────────────────────────────────────────────────────────────

export type FacilityConnectionStatus = "Connected" | "Degraded" | "Disconnected";

export interface FacilityStatusRow {
  facilityName: string;
  portal: "DHA" | "RHA" | "Both";
  status: FacilityConnectionStatus;
  lastSyncTime: string;
  recordCount: number;
  claimCount: number;
  fileCount: number;
  downloadedFilesCount: number;
  pendingFilesCount: number;
}

export interface FacilityStatusVM {
  facilities: FacilityStatusRow[];
  connectedCount: number;
  degradedCount: number;
  disconnectedCount: number;
  totalRecords: number;
  totalClaimCount: number;
  totalFiles: number;
  lastSyncTime: string;
}

// BI Reports (RCMDashboardViewModel)
export type Tone = "teal" | "gold" | "green" | "blue" | "coral";
export interface DashboardMetric {
  label: string;
  value: string;
  delta: string;
  deltaDir: "up" | "down" | "flat";
  icon: string; // lucide icon key
  tone: Tone;
}
export interface TrendPoint {
  label: string;
  value: number;
}
export interface BreakdownItem {
  label: string;
  value: number;
  detail: string;
}
export type InsightStatus = "Good" | "Watch" | "Action" | "Info";
export interface Insight {
  title: string;
  detail: string;
  status: InsightStatus;
}
export interface TabData {
  metrics: DashboardMetric[];
  trend: TrendPoint[];
  breakdown: BreakdownItem[];
  insights: Insight[];
  summary: string;
}

// Portal Files (raw file inbox) / Claim Extracts (parsed rows)
export interface PortalFileRow {
  facility: string;
  fileName: string;
  type: "Claim" | "Remittance" | "Prior Request" | "Prior Auth";
  direction: "Sent" | "Received";
  syncedAt: string;
  downloaded: boolean;
}
export interface ClaimExtractRow {
  facility: string;
  claimId: string;
  kind: "Submission" | "Remittance";
  payer: string;
  netAmount: number;
  matched: boolean;
}

// Resubmission
export interface DenialReasonRow {
  code: string;
  reason: string;
  count: number;
  amount: number;
}
export interface WorkloadRow {
  claimId: string;
  facility: string;
  analyst: string;
  priority: "High" | "Medium" | "Low";
  status: "Open" | "In Review" | "Resubmitted" | "Closed";
  ageDays: number;
  amount: number;
}

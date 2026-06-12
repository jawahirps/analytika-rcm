// Per-tab identity copied verbatim from the production RCMDashboard.cshtml.
import type { TabKey } from "../../mock/biReports";

export const tabMeta: Record<
  TabKey,
  { label: string; accent: string; accent2: string }
> = {
  submissions: { label: "Submissions", accent: "#14B8A6", accent2: "#0EA5A0" },
  resubmissions: { label: "Resubmissions", accent: "#8B5CF6", accent2: "#7C3AED" },
  remittance: { label: "Remittance", accent: "#10B981", accent2: "#059669" },
  denials: { label: "Denials", accent: "#F43F5E", accent2: "#E11D48" },
  clinicians: { label: "Clinicians", accent: "#6366F1", accent2: "#4F46E5" },
  operations: { label: "Operations", accent: "#F59E0B", accent2: "#D97706" },
  insurance: { label: "Insurance", accent: "#06B6D4", accent2: "#0891B2" },
  department: { label: "Department", accent: "#D946EF", accent2: "#C026D3" },
};

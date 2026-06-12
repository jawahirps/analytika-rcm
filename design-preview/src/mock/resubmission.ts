// SYNTHETIC PREVIEW DATA — NO PHI. Denial analytics + analyst queue.
import type { DenialReasonRow, WorkloadRow } from "./types";

export const denialReasons: DenialReasonRow[] = [
  { code: "AUTH-01", reason: "Authorization missing / expired", count: 1820, amount: 842000 },
  { code: "COV-04", reason: "Service not covered by plan", count: 1410, amount: 611000 },
  { code: "COD-12", reason: "Coding mismatch (CPT/ICD)", count: 1190, amount: 503000 },
  { code: "ELG-02", reason: "Member not eligible on DOS", count: 980, amount: 388000 },
  { code: "DUP-01", reason: "Duplicate claim", count: 760, amount: 251000 },
  { code: "DOC-07", reason: "Insufficient documentation", count: 540, amount: 198000 },
];

export const denialTrend = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"].map((m, i) => ({
  label: m,
  value: Math.round(620 + Math.sin(i) * 90 + i * 18),
}));

const analysts = ["S. Khan — Demo", "M. Ali — Demo", "R. Das — Demo", "L. Haddad — Demo"];
const facilities = ["Cedar Medical — Demo", "Marina Health — Demo", "Palm Care — Demo", "Al Noor — Demo"];

export const workload: WorkloadRow[] = Array.from({ length: 24 }).map((_, i) => {
  const pr = (["High", "Medium", "Low"] as const)[i % 3];
  const st = (["Open", "In Review", "Resubmitted", "Closed"] as const)[i % 4];
  return {
    claimId: `DEMO-CLM-${String(50100 + i)}`,
    facility: facilities[i % facilities.length],
    analyst: analysts[i % analysts.length],
    priority: pr,
    status: st,
    ageDays: (i * 3) % 21,
    amount: Math.round(500 + Math.abs(Math.cos(i)) * 4800),
  };
});

// SYNTHETIC PREVIEW DATA — NO PHI. Per-tab BI metrics/trend/breakdown/insights.
import type { TabData } from "./types";

const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const trend = (base: number, swing: number) =>
  months.map((m, i) => ({ label: m, value: Math.round(base + Math.sin(i / 1.6) * swing + i * (swing / 12)) }));

export const tabKeys = [
  "submissions",
  "resubmissions",
  "remittance",
  "denials",
  "clinicians",
  "operations",
  "insurance",
  "department",
] as const;
export type TabKey = (typeof tabKeys)[number];

export const biReports: Record<TabKey, TabData> = {
  submissions: {
    summary: "Claim submission volumes, acceptance rates and timelines across all demo facilities.",
    metrics: [
      { label: "Submitted Claims", value: "182,430", delta: "+6.2%", deltaDir: "up", icon: "send", tone: "teal" },
      { label: "Acceptance Rate", value: "94.8%", delta: "+1.1%", deltaDir: "up", icon: "check-circle", tone: "green" },
      { label: "Avg. TAT", value: "1.8 d", delta: "-0.3 d", deltaDir: "up", icon: "clock", tone: "blue" },
      { label: "Net Billed", value: "AED 24.6M", delta: "+4.4%", deltaDir: "up", icon: "wallet", tone: "gold" },
    ],
    trend: trend(14000, 2600),
    breakdown: [
      { label: "Cedar Medical — Demo", value: 44266, detail: "24% of volume" },
      { label: "Marina Health — Demo", value: 36500, detail: "20%" },
      { label: "Palm Care — Demo", value: 31100, detail: "17%" },
      { label: "Al Noor — Demo", value: 28203, detail: "15%" },
      { label: "Greenfield — Demo", value: 21010, detail: "12%" },
    ],
    insights: [
      { title: "Acceptance trending up", detail: "First-pass acceptance improved 1.1% MoM across demo payers.", status: "Good" },
      { title: "Marina TAT slipping", detail: "Submission turnaround at Marina Health rose to 2.6d — watch backlog.", status: "Watch" },
      { title: "Missing diagnosis codes", detail: "412 demo claims rejected for incomplete coding this period.", status: "Action" },
    ],
  },
  resubmissions: {
    summary: "Resubmitted claims tracking and resolution outcomes.",
    metrics: [
      { label: "Resubmitted", value: "12,084", delta: "-3.1%", deltaDir: "up", icon: "rotate-ccw", tone: "teal" },
      { label: "Recovery Rate", value: "71.3%", delta: "+2.5%", deltaDir: "up", icon: "trending-up", tone: "green" },
      { label: "Avg. Cycles", value: "1.4", delta: "0.0", deltaDir: "flat", icon: "repeat", tone: "blue" },
      { label: "Recovered", value: "AED 5.1M", delta: "+8.0%", deltaDir: "up", icon: "wallet", tone: "gold" },
    ],
    trend: trend(900, 220),
    breakdown: [
      { label: "Authorization", value: 3120, detail: "26%" },
      { label: "Coding", value: 2840, detail: "23%" },
      { label: "Eligibility", value: 2210, detail: "18%" },
      { label: "Documentation", value: 1980, detail: "16%" },
      { label: "Other", value: 1934, detail: "17%" },
    ],
    insights: [
      { title: "Recovery rate up", detail: "Resubmission recovery climbed to 71.3% — best in 6 demo months.", status: "Good" },
      { title: "Coding loops", detail: "23% of resubmissions cite coding — candidate for a pre-check rule.", status: "Watch" },
    ],
  },
  remittance: {
    summary: "Payment reconciliation and remittance analysis.",
    metrics: [
      { label: "Remits Received", value: "168,902", delta: "+5.0%", deltaDir: "up", icon: "inbox", tone: "teal" },
      { label: "Paid Ratio", value: "88.6%", delta: "+0.9%", deltaDir: "up", icon: "check-circle", tone: "green" },
      { label: "Avg. Settle", value: "12.4 d", delta: "-1.1 d", deltaDir: "up", icon: "clock", tone: "blue" },
      { label: "Collected", value: "AED 21.0M", delta: "+3.7%", deltaDir: "up", icon: "wallet", tone: "gold" },
    ],
    trend: trend(13000, 2400),
    breakdown: [
      { label: "Daman Demo", value: 62000, detail: "37%" },
      { label: "Thiqa Demo", value: 41000, detail: "24%" },
      { label: "NextCare Demo", value: 33000, detail: "20%" },
      { label: "Neuron Demo", value: 19902, detail: "12%" },
      { label: "Self-pay Demo", value: 13000, detail: "7%" },
    ],
    insights: [
      { title: "Settlement faster", detail: "Average settlement down to 12.4d across demo payers.", status: "Good" },
      { title: "Short-paid lines", detail: "AED 640k in short-paid demo lines pending reconciliation.", status: "Action" },
    ],
  },
  denials: {
    summary: "Denial patterns, root causes and trends.",
    metrics: [
      { label: "Denial Rate", value: "6.4%", delta: "+0.5%", deltaDir: "down", icon: "ban", tone: "coral" },
      { label: "Denied Value", value: "AED 3.2M", delta: "+0.4M", deltaDir: "down", icon: "wallet", tone: "gold" },
      { label: "Top Reason", value: "Auth", delta: "26%", deltaDir: "flat", icon: "alert-triangle", tone: "coral" },
      { label: "Recoverable", value: "AED 2.1M", delta: "66%", deltaDir: "up", icon: "trending-up", tone: "green" },
    ],
    trend: trend(700, 180),
    breakdown: [
      { label: "Authorization missing", value: 1820, detail: "26%" },
      { label: "Service not covered", value: 1410, detail: "20%" },
      { label: "Coding mismatch", value: 1190, detail: "17%" },
      { label: "Eligibility", value: 980, detail: "14%" },
      { label: "Duplicate", value: 760, detail: "11%" },
    ],
    insights: [
      { title: "Auth denials rising", detail: "Authorization denials up 4% — tighten pre-auth checks.", status: "Action" },
      { title: "Recoverable backlog", detail: "AED 2.1M recoverable denials within the appeal window.", status: "Watch" },
    ],
  },
  clinicians: {
    summary: "Clinician productivity and claim performance.",
    metrics: [
      { label: "Active Clinicians", value: "248", delta: "+6", deltaDir: "up", icon: "stethoscope", tone: "teal" },
      { label: "Claims / Clinician", value: "735", delta: "+12", deltaDir: "up", icon: "activity", tone: "blue" },
      { label: "Avg. Accept", value: "93.1%", delta: "+0.6%", deltaDir: "up", icon: "check-circle", tone: "green" },
      { label: "Top Earner", value: "AED 1.2M", delta: "demo", deltaDir: "flat", icon: "award", tone: "gold" },
    ],
    trend: trend(600, 120),
    breakdown: [
      { label: "Dr. A. — Demo", value: 1240, detail: "Cardiology" },
      { label: "Dr. B. — Demo", value: 1110, detail: "Orthopedics" },
      { label: "Dr. C. — Demo", value: 980, detail: "Internal Med" },
      { label: "Dr. D. — Demo", value: 870, detail: "Dermatology" },
      { label: "Dr. E. — Demo", value: 760, detail: "Pediatrics" },
    ],
    insights: [
      { title: "Productivity steady", detail: "Claims per clinician up modestly across demo cohort.", status: "Good" },
    ],
  },
  operations: {
    summary: "Operational KPIs, TAT metrics and SLA compliance.",
    metrics: [
      { label: "SLA Compliance", value: "96.2%", delta: "+0.8%", deltaDir: "up", icon: "gauge", tone: "green" },
      { label: "Files Pending", value: "1,104", delta: "-220", deltaDir: "up", icon: "inbox", tone: "gold" },
      { label: "Sync Health", value: "8 / 10", delta: "demo", deltaDir: "flat", icon: "server", tone: "teal" },
      { label: "Avg. Cycle", value: "3.1 d", delta: "-0.2 d", deltaDir: "up", icon: "clock", tone: "blue" },
    ],
    trend: trend(500, 90),
    breakdown: [
      { label: "Submission", value: 4200, detail: "downloaded" },
      { label: "Remittance", value: 3800, detail: "downloaded" },
      { label: "Prior Auth", value: 1200, detail: "downloaded" },
      { label: "Prior Request", value: 900, detail: "downloaded" },
      { label: "Pending", value: 1104, detail: "queued" },
    ],
    insights: [
      { title: "Pending queue shrinking", detail: "Pending downloads dropped 220 after the overnight run.", status: "Good" },
      { title: "2 facilities degraded", detail: "Marina & Apollo demo nodes need a credential re-check.", status: "Action" },
    ],
  },
  insurance: {
    summary: "Payer-wise approval, rejection and payment behavior.",
    metrics: [
      { label: "Payers", value: "14", delta: "demo", deltaDir: "flat", icon: "shield", tone: "teal" },
      { label: "Approval", value: "91.4%", delta: "+0.7%", deltaDir: "up", icon: "check-circle", tone: "green" },
      { label: "Avg. Pay Days", value: "18.7", delta: "-0.9", deltaDir: "up", icon: "clock", tone: "blue" },
      { label: "Outstanding", value: "AED 6.8M", delta: "demo", deltaDir: "flat", icon: "wallet", tone: "gold" },
    ],
    trend: trend(800, 160),
    breakdown: [
      { label: "Daman Demo", value: 91, detail: "approval %" },
      { label: "Thiqa Demo", value: 89, detail: "approval %" },
      { label: "NextCare Demo", value: 87, detail: "approval %" },
      { label: "Neuron Demo", value: 84, detail: "approval %" },
      { label: "Aafiya Demo", value: 80, detail: "approval %" },
    ],
    insights: [
      { title: "Neuron slow to pay", detail: "Neuron Demo averaging 27 pay-days — escalate aging buckets.", status: "Watch" },
    ],
  },
  department: {
    summary: "Department and facility performance breakdown.",
    metrics: [
      { label: "Departments", value: "32", delta: "demo", deltaDir: "flat", icon: "building", tone: "teal" },
      { label: "Top Dept Net", value: "AED 4.1M", delta: "Cardiology", deltaDir: "flat", icon: "wallet", tone: "gold" },
      { label: "Avg. Accept", value: "92.6%", delta: "+0.4%", deltaDir: "up", icon: "check-circle", tone: "green" },
      { label: "Busiest", value: "Radiology", delta: "demo", deltaDir: "flat", icon: "activity", tone: "blue" },
    ],
    trend: trend(700, 140),
    breakdown: [
      { label: "Cardiology", value: 4100, detail: "net (k)" },
      { label: "Radiology", value: 3600, detail: "net (k)" },
      { label: "Orthopedics", value: 3050, detail: "net (k)" },
      { label: "Internal Med", value: 2700, detail: "net (k)" },
      { label: "Dermatology", value: 2100, detail: "net (k)" },
    ],
    insights: [
      { title: "Cardiology leads", detail: "Cardiology contributes the largest demo net across facilities.", status: "Good" },
    ],
  },
};

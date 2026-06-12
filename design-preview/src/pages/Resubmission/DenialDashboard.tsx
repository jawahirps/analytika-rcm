import { Ban, ShieldCheck, TrendingUp, Wallet } from "lucide-react";
import { AreaTrend } from "../../components/charts/AreaTrend";
import { Donut } from "../../components/charts/Donut";
import { DataTable, type Column } from "../../components/ui/DataTable";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatCard } from "../../components/ui/StatCard";
import { denialReasons, denialTrend } from "../../mock/resubmission";
import type { DashboardMetric, DenialReasonRow } from "../../mock/types";
import { useEffect } from "react";

const metrics: DashboardMetric[] = [
  { label: "Denial Rate", value: "6.4%", delta: "+0.5%", deltaDir: "down", icon: "ban", tone: "coral" },
  { label: "Denied Value", value: "AED 3.2M", delta: "+0.4M", deltaDir: "down", icon: "wallet", tone: "gold" },
  { label: "Recoverable", value: "AED 2.1M", delta: "66%", deltaDir: "up", icon: "trending-up", tone: "green" },
  { label: "Appeals Open", value: "184", delta: "demo", deltaDir: "flat", icon: "shield", tone: "blue" },
];

export function DenialDashboard() {
  useEffect(() => {
    document.documentElement.style.setProperty("--accent", "#F43F5E");
    document.documentElement.style.setProperty("--accent2", "#E11D48");
  }, []);

  const columns: Column<DenialReasonRow>[] = [
    { header: "Code", cell: (r) => <span className="font-mono text-xs font-bold">{r.code}</span> },
    { header: "Reason", cell: (r) => <span className="font-medium text-ink">{r.reason}</span> },
    { header: "Count", align: "end", cell: (r) => r.count.toLocaleString() },
    { header: "Value (AED)", align: "end", cell: (r) => r.amount.toLocaleString() },
  ];

  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-xl font-extrabold text-ink">
          <Ban size={20} className="text-coral" /> Denial Dashboard
        </h1>
        <p className="text-sm text-muted">Denial patterns, root causes and recoverable value (synthetic).</p>
      </div>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {metrics.map((m) => (
          <StatCard key={m.label} m={m} />
        ))}
      </div>

      <div className="grid gap-4 lg:grid-cols-3">
        <SectionCard title="Denial categories" className="lg:col-span-1">
          <Donut data={denialReasons.map((d) => ({ label: d.reason, value: d.count }))} />
        </SectionCard>
        <SectionCard title="Denial trend" subtitle="6 months" className="lg:col-span-2">
          <AreaTrend data={denialTrend} />
        </SectionCard>
      </div>

      <SectionCard title="Top denial reasons">
        <DataTable columns={columns} rows={denialReasons} />
      </SectionCard>
    </div>
  );
}

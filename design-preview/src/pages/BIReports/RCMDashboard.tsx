import { CircleAlert, CircleCheck, CircleHelp, Download, TriangleAlert } from "lucide-react";
import { useEffect } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { AreaTrend } from "../../components/charts/AreaTrend";
import { BreakdownBars } from "../../components/charts/BreakdownBars";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatCard } from "../../components/ui/StatCard";
import { FilterBar } from "../../components/ui/FilterBar";
import { biReports, tabKeys, type TabKey } from "../../mock/biReports";
import type { InsightStatus } from "../../mock/types";
import { tabMeta } from "./tabMeta";

const statusStyle: Record<InsightStatus, { cls: string; Icon: typeof CircleCheck }> = {
  Good: { cls: "text-success bg-success/10", Icon: CircleCheck },
  Watch: { cls: "text-warning bg-warning/10", Icon: TriangleAlert },
  Action: { cls: "text-danger bg-danger/10", Icon: CircleAlert },
  Info: { cls: "text-info bg-info/10", Icon: CircleHelp },
};

export function RCMDashboard() {
  const { tab } = useParams();
  const navigate = useNavigate();
  const active: TabKey = (tabKeys as readonly string[]).includes(tab ?? "")
    ? (tab as TabKey)
    : "submissions";
  const meta = tabMeta[active];
  const data = biReports[active];

  // Drive the live accent variables (recolors charts + accents instantly).
  useEffect(() => {
    const el = document.documentElement;
    el.style.setProperty("--accent", meta.accent);
    el.style.setProperty("--accent2", meta.accent2);
  }, [meta]);

  return (
    <div className="space-y-4">
      {/* Tab rail */}
      <div className="flex flex-wrap gap-2">
        {tabKeys.map((k) => {
          const m = tabMeta[k];
          const on = k === active;
          return (
            <button
              key={k}
              onClick={() => navigate(`/reports/${k}`)}
              className="rounded-full px-3.5 py-1.5 text-sm font-bold transition-all"
              style={
                on
                  ? { background: m.accent, color: "#fff", boxShadow: `0 8px 20px ${m.accent}55` }
                  : { background: `color-mix(in srgb, ${m.accent} 12%, transparent)`, color: m.accent }
              }
            >
              {m.label}
            </button>
          );
        })}
      </div>

      {/* Hero */}
      <div
        className="relative overflow-hidden rounded-2xl p-6 text-white"
        style={{
          background: `linear-gradient(120deg, ${meta.accent2} 0%, ${meta.accent} 70%)`,
          boxShadow: `0 18px 44px ${meta.accent}55`,
        }}
      >
        <div className="relative flex flex-wrap items-center justify-between gap-4">
          <div className="max-w-2xl">
            <span className="inline-flex rounded-full border border-white/30 bg-white/20 px-2.5 py-1 text-[11px] font-extrabold uppercase tracking-widest">
              BI Reports
            </span>
            <h1 className="mt-2 text-2xl font-extrabold tracking-tight sm:text-3xl">
              {meta.label}
            </h1>
            <p className="mt-1 text-sm text-white/85">{data.summary}</p>
          </div>
          <button className="inline-flex items-center gap-2 rounded-xl bg-white/95 px-3 py-2 text-sm font-bold" style={{ color: meta.accent2 }}>
            <Download size={15} /> Export
          </button>
        </div>
      </div>

      <FilterBar />

      {/* KPI grid */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {data.metrics.map((m) => (
          <StatCard key={m.label} m={m} />
        ))}
      </div>

      {/* Trend + breakdown */}
      <div className="grid gap-4 lg:grid-cols-3">
        <SectionCard title="Monthly trend" subtitle="Synthetic, 12 months" className="lg:col-span-2">
          <AreaTrend key={active} data={data.trend} />
        </SectionCard>
        <SectionCard title="Breakdown">
          <BreakdownBars items={data.breakdown} />
        </SectionCard>
      </div>

      {/* Insights */}
      <SectionCard title="Insights">
        <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
          {data.insights.map((ins) => {
            const s = statusStyle[ins.status];
            return (
              <div key={ins.title} className="rounded-xl border border-border bg-bg p-3">
                <div className="flex items-center gap-2">
                  <span className={`grid h-7 w-7 place-items-center rounded-lg ${s.cls}`}>
                    <s.Icon size={15} />
                  </span>
                  <span className="text-sm font-bold text-ink">{ins.title}</span>
                </div>
                <p className="mt-2 text-xs leading-relaxed text-muted">{ins.detail}</p>
              </div>
            );
          })}
        </div>
      </SectionCard>
    </div>
  );
}

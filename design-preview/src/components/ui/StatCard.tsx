import {
  Activity,
  AlertTriangle,
  Award,
  Ban,
  Building,
  CheckCircle,
  Clock,
  Gauge,
  Inbox,
  Repeat,
  RotateCcw,
  Send,
  Server,
  Shield,
  Stethoscope,
  TrendingDown,
  TrendingUp,
  Wallet,
  type LucideIcon,
} from "lucide-react";
import type { DashboardMetric, Tone } from "../../mock/types";

const icons: Record<string, LucideIcon> = {
  send: Send, "check-circle": CheckCircle, clock: Clock, wallet: Wallet,
  "rotate-ccw": RotateCcw, "trending-up": TrendingUp, repeat: Repeat, inbox: Inbox,
  ban: Ban, "alert-triangle": AlertTriangle, stethoscope: Stethoscope, activity: Activity,
  award: Award, gauge: Gauge, server: Server, shield: Shield, building: Building,
};

const toneColor: Record<Tone, string> = {
  teal: "var(--teal)", gold: "var(--gold)", green: "var(--success)",
  blue: "var(--info)", coral: "var(--coral)",
};

export function StatCard({ m }: { m: DashboardMetric }) {
  const Icon = icons[m.icon] ?? Activity;
  const c = toneColor[m.tone];
  const Delta = m.deltaDir === "down" ? TrendingDown : TrendingUp;
  const deltaCls =
    m.deltaDir === "down"
      ? "text-danger"
      : m.deltaDir === "up"
      ? "text-success"
      : "text-muted";
  return (
    <div className="rounded-xl border border-border bg-surface p-4 shadow-soft">
      <div className="flex items-start justify-between">
        <span
          className="grid h-10 w-10 place-items-center rounded-lg"
          style={{ background: `color-mix(in srgb, ${c} 14%, transparent)`, color: c }}
        >
          <Icon size={20} />
        </span>
        <span className={`inline-flex items-center gap-1 text-xs font-bold ${deltaCls}`}>
          {m.deltaDir !== "flat" && <Delta size={13} />}
          {m.delta}
        </span>
      </div>
      <div className="mt-3 text-2xl font-extrabold tracking-tight text-ink tabular-nums">
        {m.value}
      </div>
      <div className="mt-0.5 text-xs font-semibold uppercase tracking-wide text-muted">
        {m.label}
      </div>
    </div>
  );
}

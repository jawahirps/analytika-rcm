import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import type { TrendPoint } from "../../mock/types";

// Reads the live --accent CSS variable so it recolors with the active BI tab.
export function AreaTrend({ data }: { data: TrendPoint[] }) {
  const accent =
    getComputedStyle(document.documentElement).getPropertyValue("--accent").trim() ||
    "#14b8a6";
  const grid = getComputedStyle(document.documentElement)
    .getPropertyValue("--border")
    .trim();
  const muted = getComputedStyle(document.documentElement)
    .getPropertyValue("--text-muted")
    .trim();
  return (
    <div className="h-64 w-full">
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart data={data} margin={{ top: 8, right: 8, left: -16, bottom: 0 }}>
          <defs>
            <linearGradient id="accentFill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={accent} stopOpacity={0.42} />
              <stop offset="100%" stopColor={accent} stopOpacity={0.02} />
            </linearGradient>
          </defs>
          <CartesianGrid stroke={grid} vertical={false} />
          <XAxis dataKey="label" tick={{ fill: muted, fontSize: 11 }} tickLine={false} axisLine={false} />
          <YAxis tick={{ fill: muted, fontSize: 11 }} tickLine={false} axisLine={false} width={48} />
          <Tooltip
            contentStyle={{
              background: "var(--surface)",
              border: "1px solid var(--border)",
              borderRadius: 10,
              color: "var(--text)",
              fontSize: 12,
            }}
          />
          <Area
            type="monotone"
            dataKey="value"
            stroke={accent}
            strokeWidth={2.5}
            fill="url(#accentFill)"
          />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}

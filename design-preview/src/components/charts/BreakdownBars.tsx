import type { BreakdownItem } from "../../mock/types";

const palette = [
  "#14B8A6", "#8B5CF6", "#F59E0B", "#F43F5E", "#06B6D4", "#6366F1", "#22C55E", "#D946EF",
];

export function BreakdownBars({ items }: { items: BreakdownItem[] }) {
  const max = Math.max(1, ...items.map((i) => i.value));
  return (
    <ul className="space-y-3">
      {items.map((it, i) => (
        <li key={it.label}>
          <div className="mb-1 flex items-center justify-between text-sm">
            <span className="truncate font-medium text-ink">{it.label}</span>
            <span className="ms-2 shrink-0 tabular-nums text-muted">
              {it.value.toLocaleString()}{" "}
              <span className="text-xs">· {it.detail}</span>
            </span>
          </div>
          <div className="h-2.5 overflow-hidden rounded-full bg-ink/[0.06]">
            <div
              className="h-full rounded-full transition-all"
              style={{
                width: `${(it.value / max) * 100}%`,
                background: palette[i % palette.length],
              }}
            />
          </div>
        </li>
      ))}
    </ul>
  );
}

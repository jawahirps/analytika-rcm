export function ProgressBar({
  value,
  max,
  color = "var(--teal)",
}: {
  value: number;
  max: number;
  color?: string;
}) {
  const pct = max <= 0 ? 0 : Math.min(100, Math.round((value / max) * 100));
  return (
    <div className="flex items-center gap-2">
      <div className="h-2 flex-1 overflow-hidden rounded-full bg-ink/10">
        <div
          className="h-full rounded-full transition-all"
          style={{ width: `${pct}%`, background: color }}
        />
      </div>
      <span className="w-9 text-end text-xs font-semibold tabular-nums text-muted">
        {pct}%
      </span>
    </div>
  );
}

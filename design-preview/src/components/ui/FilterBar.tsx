import { Search, SlidersHorizontal } from "lucide-react";

export function FilterBar({
  options = ["All facilities", "Cedar Medical — Demo", "Marina Health — Demo", "Palm Care — Demo"],
}: {
  options?: string[];
}) {
  return (
    <div className="flex flex-wrap items-center gap-2 rounded-xl border border-border bg-surface p-2 shadow-soft">
      <div className="flex items-center gap-2 rounded-lg border border-border bg-bg px-2.5 py-1.5">
        <Search size={15} className="text-muted" />
        <input
          className="w-44 bg-transparent text-sm text-ink outline-none placeholder:text-muted"
          placeholder="Claim, file, payer…"
        />
      </div>
      <select className="rounded-lg border border-border bg-bg px-2.5 py-1.5 text-sm text-ink outline-none">
        {options.map((o) => (
          <option key={o}>{o}</option>
        ))}
      </select>
      <input
        type="date"
        className="rounded-lg border border-border bg-bg px-2.5 py-1.5 text-sm text-ink outline-none"
      />
      <span className="text-xs text-muted">→</span>
      <input
        type="date"
        className="rounded-lg border border-border bg-bg px-2.5 py-1.5 text-sm text-ink outline-none"
      />
      <button className="ms-auto inline-flex items-center gap-1.5 rounded-lg bg-navy px-3 py-1.5 text-sm font-semibold text-white">
        <SlidersHorizontal size={14} />
        Apply
      </button>
    </div>
  );
}

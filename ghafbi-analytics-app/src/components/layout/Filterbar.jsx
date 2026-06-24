import { CalendarDays, ChevronDown, SlidersHorizontal } from "lucide-react";
import { FILTER_CHIPS, PERIOD_OPTIONS } from "@/data/navigation";
import { SegmentedControl } from "@/components/ui/SegmentedControl";

export function Filterbar({ period, setPeriod, filtersOpen, setFiltersOpen }) {
  return (
    <>
      <section className="filterbar" aria-label="Dashboard filters">
        <button
          type="button"
          className={`toolbar-btn ${filtersOpen ? "selected-control" : ""}`}
          aria-expanded={filtersOpen}
          onClick={() => setFiltersOpen(!filtersOpen)}
        >
          <SlidersHorizontal size={16} aria-hidden="true" />
          Filters
        </button>
        <button type="button" className="toolbar-btn date-btn">
          <CalendarDays size={16} aria-hidden="true" />
          May 18 - May 24, 2025
          <ChevronDown size={14} aria-hidden="true" />
        </button>
        <SegmentedControl className="period-tabs" options={PERIOD_OPTIONS} value={period} onChange={setPeriod} />
        <button type="button" className="toolbar-btn compact" aria-label="Advanced filters">
          <SlidersHorizontal size={16} />
        </button>
      </section>
      {filtersOpen && (
        <section className="filter-drawer" aria-label="Active filters">
          {FILTER_CHIPS.map((item) => (
            <button key={item} type="button">
              {item}
            </button>
          ))}
        </section>
      )}
    </>
  );
}

import { Lock } from "lucide-react";
import { NAV_ITEMS } from "@/data/navigation";

export function Sidebar({ active, drawerOpen, onSelect }) {
  return (
    <aside className={`sidebar ${drawerOpen ? "open" : ""}`} aria-label="Application sidebar">
      <div className="brand">
        <div className="brand-mark" aria-hidden="true">
          <span />
          <span />
          <span />
        </div>
        <div>
          <strong>Ghafbi</strong>
          <small>Analytics</small>
        </div>
      </div>

      <nav className="side-nav" aria-label="Primary navigation">
        {NAV_ITEMS.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            className={active === id ? "active" : ""}
            aria-current={active === id ? "page" : undefined}
            onClick={() => onSelect(id)}
          >
            <Icon size={18} aria-hidden="true" />
            <span>{label}</span>
          </button>
        ))}
      </nav>

      <div className="workspace-card">
        <div className="workspace-icon">
          <Lock size={16} aria-hidden="true" />
        </div>
        <strong>Ghaf Healthcare</strong>
        <span>Enterprise workspace</span>
      </div>
    </aside>
  );
}

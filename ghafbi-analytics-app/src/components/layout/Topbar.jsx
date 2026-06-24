import { ChevronDown, Download, Menu, Moon, RefreshCcw, Search, Sun, Users } from "lucide-react";

export function Topbar({ activeNav, theme, onToggleTheme, onMenu, onAction }) {
  return (
    <header className="topbar">
      <button type="button" className="icon-btn mobile-menu" aria-label="Open menu" onClick={onMenu}>
        <Menu size={20} />
      </button>
      <div className="page-title-block">
        <h1>{activeNav.label}</h1>
        <p>{activeNav.description}</p>
      </div>
      <div className="top-actions">
        <label className="searchbox">
          <Search size={16} aria-hidden="true" />
          <input type="search" placeholder="Search dashboards, reports, metrics..." aria-label="Search workspace" />
        </label>
        <button type="button" className="primary-btn" onClick={() => onAction("Export")}>
          <Download size={16} aria-hidden="true" />
          Export
          <ChevronDown size={14} aria-hidden="true" />
        </button>
        <button type="button" className="toolbar-btn" onClick={() => onAction("Share")}>
          <Users size={16} aria-hidden="true" />
          Share
        </button>
        <button
          type="button"
          className="toolbar-btn compact theme-toggle"
          aria-label={theme === "dark" ? "Switch to light theme" : "Switch to dark theme"}
          onClick={onToggleTheme}
        >
          {theme === "dark" ? <Sun size={16} /> : <Moon size={16} />}
        </button>
        <button type="button" className="toolbar-btn compact" aria-label="Refresh" onClick={() => onAction("Refresh")}>
          <RefreshCcw size={16} />
        </button>
      </div>
    </header>
  );
}

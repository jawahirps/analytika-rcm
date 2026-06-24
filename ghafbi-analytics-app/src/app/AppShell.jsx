import { useMemo, useState } from "react";
import { DASHBOARD_DATA } from "@/data/dashboardData";
import { NAV_ITEMS } from "@/data/navigation";
import { useToast } from "@/hooks/useToast";
import { useTheme } from "@/hooks/useTheme";
import { Sidebar } from "@/components/layout/Sidebar";
import { Topbar } from "@/components/layout/Topbar";
import { Filterbar } from "@/components/layout/Filterbar";
import { MobileNav } from "@/components/layout/MobileNav";
import { MetricCard } from "@/components/ui/MetricCard";
import { Toast } from "@/components/ui/Toast";
import { TrendPanel } from "@/components/panels/TrendPanel";
import { AiPanel } from "@/components/panels/AiPanel";
import { DataHealthPanel } from "@/components/panels/DataHealthPanel";
import { ActivityPanel } from "@/components/panels/ActivityPanel";
import { TemplatesPanel } from "@/components/panels/TemplatesPanel";
import { AlertsPanel } from "@/components/panels/AlertsPanel";
import { QuickActions } from "@/components/panels/QuickActions";
import { DhpoFetchPanel } from "@/components/panels/DhpoFetchPanel";

export function AppShell() {
  const [active, setActive] = useState("Overview");
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [period, setPeriod] = useState("7D");
  const [chartMetric, setChartMetric] = useState("Revenue");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const { message, show } = useToast();
  const { theme, toggle: toggleTheme } = useTheme();

  const activeNav = useMemo(
    () => NAV_ITEMS.find((item) => item.id === active) ?? NAV_ITEMS[0],
    [active],
  );
  const data = DASHBOARD_DATA[active] ?? DASHBOARD_DATA.Overview;

  function selectNav(id) {
    setActive(id);
    setDrawerOpen(false);
    show(`${id} workspace loaded`);
  }

  function runAction(label) {
    show(`${label} started`);
  }

  return (
    <div className="app-shell">
      <Sidebar active={active} drawerOpen={drawerOpen} onSelect={selectNav} />
      {drawerOpen && (
        <button type="button" className="scrim" aria-label="Close menu" onClick={() => setDrawerOpen(false)} />
      )}

      <main className="main-panel">
        <Topbar
          activeNav={activeNav}
          theme={theme}
          onToggleTheme={toggleTheme}
          onMenu={() => setDrawerOpen(true)}
          onAction={runAction}
        />
        <Filterbar
          period={period}
          setPeriod={setPeriod}
          filtersOpen={filtersOpen}
          setFiltersOpen={setFiltersOpen}
        />

        <section className="metric-strip" aria-label="Key metrics">
          {data.metrics.map((metric, index) => (
            <MetricCard key={metric.label} metric={metric} index={index} />
          ))}
        </section>

        <section className="workspace-grid">
          {active === "Data" && <DhpoFetchPanel onAction={runAction} />}
          <TrendPanel chartMetric={chartMetric} setChartMetric={setChartMetric} period={period} />
          <AiPanel onAction={runAction} />
          <DataHealthPanel />
          <ActivityPanel rows={data.rows} />
          <TemplatesPanel />
          <AlertsPanel />
          <QuickActions onAction={runAction} />
        </section>

        <MobileNav active={active} onSelect={selectNav} />
      </main>

      <Toast message={message} />
    </div>
  );
}

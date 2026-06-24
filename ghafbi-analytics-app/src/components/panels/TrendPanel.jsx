import { CHART_PATHS } from "@/data/dashboardData";
import { CHART_METRICS } from "@/data/navigation";
import { Panel, PanelHead } from "@/components/ui/Panel";
import { SegmentedControl } from "@/components/ui/SegmentedControl";

export function TrendPanel({ chartMetric, setChartMetric, period }) {
  const path = CHART_PATHS[chartMetric];

  return (
    <Panel wide className="chart-panel">
      <PanelHead
        title={`${chartMetric} Trend`}
        subtitle={`Actuals, forecast, and variance for ${period}`}
        action={
          <SegmentedControl options={CHART_METRICS} value={chartMetric} onChange={setChartMetric} />
        }
      />
      <div className="chart">
        <svg viewBox="0 0 760 260" role="img" aria-label={`${chartMetric} trend line chart`}>
          <defs>
            <linearGradient id="chart-area" x1="0" x2="0" y1="0" y2="1">
              <stop offset="0%" stopColor="var(--chart-line)" stopOpacity="0.38" />
              <stop offset="100%" stopColor="var(--chart-line)" stopOpacity="0" />
            </linearGradient>
          </defs>
          {[40, 90, 140, 190, 240].map((y) => (
            <line key={y} x1="34" x2="732" y1={y} y2={y} className="grid-line" />
          ))}
          <path className="area" d={`${path} L724 240 L40 240 Z`} />
          <path className="line-main" d={path} />
          <path
            className="line-compare"
            d="M40 206 C122 184 150 154 214 150 C278 146 304 124 374 130 C468 136 482 104 552 104 C624 104 660 88 724 94"
          />
          {[40, 196, 360, 532, 724].map((x, idx) => (
            <circle key={x} cx={x} cy={[190, 126, 96, 72, 54][idx]} r="5" className="chart-dot" />
          ))}
        </svg>
      </div>
    </Panel>
  );
}

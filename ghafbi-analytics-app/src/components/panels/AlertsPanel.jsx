import { AlertTriangle } from "lucide-react";
import { Panel, PanelHead } from "@/components/ui/Panel";

const ALERTS = [
  { label: "Urgent", value: "2", text: "Revenue sync and claim backlog" },
  { label: "Warning", value: "6", text: "Data freshness and mapping issues" },
  { label: "Resolved", value: "19", text: "Closed in the last 7 days" },
];

export function AlertsPanel() {
  return (
    <Panel className="alerts-panel">
      <PanelHead compact title="Alerts Summary" action={<AlertTriangle size={18} aria-hidden="true" />} />
      <div className="alert-stack">
        {ALERTS.map((alert) => (
          <div className="alert-row" key={alert.label}>
            <strong>{alert.value}</strong>
            <div>
              <span>{alert.label}</span>
              <em>{alert.text}</em>
            </div>
          </div>
        ))}
      </div>
    </Panel>
  );
}

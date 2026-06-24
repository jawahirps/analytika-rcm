import { Bell, Database, FileText, LayoutDashboard, ShieldCheck } from "lucide-react";
import { Panel, PanelHead } from "@/components/ui/Panel";

const ACTIONS = [
  { icon: LayoutDashboard, label: "Create Dashboard", action: "Create dashboard" },
  { icon: FileText, label: "Create Report", action: "Create report" },
  { icon: Database, label: "Add Data Source", action: "Add data source" },
  { icon: Bell, label: "Set Alert", action: "Set alert" },
];

export function QuickActions({ onAction }) {
  return (
    <Panel className="quick-actions">
      <PanelHead compact title="Quick Actions" action={<ShieldCheck size={18} aria-hidden="true" />} />
      <div className="quick-grid">
        {ACTIONS.map(({ icon: Icon, label, action }) => (
          <button key={action} type="button" onClick={() => onAction(action)}>
            <Icon size={17} aria-hidden="true" />
            {label}
          </button>
        ))}
      </div>
    </Panel>
  );
}

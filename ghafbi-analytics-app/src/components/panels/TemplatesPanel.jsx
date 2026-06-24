import { Activity, PlugZap, Share2, Users } from "lucide-react";
import { Panel, PanelHead } from "@/components/ui/Panel";

const TEMPLATES = [
  { icon: Users, title: "Executive Reporting", text: "Board pack with revenue, cost, and pipeline." },
  { icon: Activity, title: "Operations Analytics", text: "Throughput, denials, and team workload." },
  { icon: PlugZap, title: "Marketing Analytics", text: "Campaign ROI, channels, and attribution." },
];

export function TemplatesPanel() {
  return (
    <Panel>
      <PanelHead compact title="Dashboard Templates" action={<Share2 size={18} aria-hidden="true" />} />
      <div className="template-list">
        {TEMPLATES.map(({ icon: Icon, title, text }) => (
          <div className="template" key={title}>
            <Icon size={18} aria-hidden="true" />
            <div>
              <strong>{title}</strong>
              <span>{text}</span>
            </div>
          </div>
        ))}
      </div>
    </Panel>
  );
}

import { MoreHorizontal } from "lucide-react";
import { DATA_SOURCES } from "@/data/dashboardData";
import { Panel, PanelHead } from "@/components/ui/Panel";

export function DataHealthPanel() {
  return (
    <Panel>
      <PanelHead
        compact
        title="Data Health"
        action={
          <button type="button" className="icon-btn" aria-label="More data health actions">
            <MoreHorizontal size={18} />
          </button>
        }
      />
      <div className="health-ring">
        <div className="ring">97%</div>
        <div>
          <strong>Healthy</strong>
          <span>18 sources monitored</span>
        </div>
      </div>
      <div className="source-list">
        {DATA_SOURCES.map(([name, status, score]) => (
          <div className="source-row" key={name}>
            <span>{name}</span>
            <em className={status === "Warning" ? "warn" : "good"}>{status}</em>
            <div className="bar">
              <i style={{ width: `${score}%` }} />
            </div>
          </div>
        ))}
      </div>
    </Panel>
  );
}

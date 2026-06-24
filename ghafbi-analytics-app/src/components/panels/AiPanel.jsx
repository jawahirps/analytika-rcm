import { Bot, Sparkles } from "lucide-react";
import { AI_INSIGHTS } from "@/data/dashboardData";
import { Insight } from "@/components/ui/Insight";
import { Panel, PanelHead } from "@/components/ui/Panel";

export function AiPanel({ onAction }) {
  return (
    <Panel className="ai-panel">
      <PanelHead title="Ask Ghafbi AI" subtitle="Recommended next actions" action={<Bot size={20} aria-hidden="true" />} />
      <div className="insight-list">
        {AI_INSIGHTS.map((insight) => (
          <Insight key={insight.title} {...insight} />
        ))}
      </div>
      <button type="button" className="ask-btn" onClick={() => onAction("AI question")}>
        <Sparkles size={16} aria-hidden="true" />
        Ask a question
      </button>
    </Panel>
  );
}

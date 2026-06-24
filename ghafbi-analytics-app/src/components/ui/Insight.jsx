import { CircleCheck } from "lucide-react";

export function Insight({ tone, title, text }) {
  return (
    <div className={`insight ${tone}`}>
      <CircleCheck size={16} aria-hidden="true" />
      <div>
        <strong>{title}</strong>
        <span>{text}</span>
      </div>
    </div>
  );
}

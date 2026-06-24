export function Panel({ className = "", children, wide = false }) {
  return <article className={`panel ${wide ? "wide" : ""} ${className}`.trim()}>{children}</article>;
}

export function PanelHead({ title, subtitle, action, compact = false }) {
  return (
    <div className={`panel-head ${compact ? "compact-head" : ""}`.trim()}>
      <div>
        {title && <h2>{title}</h2>}
        {subtitle && <p>{subtitle}</p>}
      </div>
      {action}
    </div>
  );
}

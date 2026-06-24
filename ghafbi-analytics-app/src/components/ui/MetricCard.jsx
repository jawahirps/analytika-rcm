export function MetricCard({ metric, index = 0 }) {
  return (
    <article className="metric" style={{ animationDelay: `${index * 60}ms` }}>
      <div>
        <span>{metric.label}</span>
        <strong>{metric.value}</strong>
        <em className={metric.tone}>{metric.change}</em>
      </div>
      <svg viewBox="0 0 94 44" aria-hidden="true">
        <path d="M4 34 C18 20 26 28 38 18 C50 8 60 20 72 11 C82 5 86 9 90 6" />
      </svg>
    </article>
  );
}

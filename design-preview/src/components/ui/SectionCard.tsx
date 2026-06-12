export function SectionCard({
  title,
  subtitle,
  right,
  children,
  className = "",
}: {
  title?: string;
  subtitle?: string;
  right?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <section
      className={`rounded-xl border border-border bg-surface shadow-soft ${className}`}
    >
      {(title || right) && (
        <header className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
          <div>
            {title && (
              <h3 className="text-sm font-bold text-ink">{title}</h3>
            )}
            {subtitle && (
              <p className="mt-0.5 text-xs text-muted">{subtitle}</p>
            )}
          </div>
          {right}
        </header>
      )}
      <div className="p-4">{children}</div>
    </section>
  );
}

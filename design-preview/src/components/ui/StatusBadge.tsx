type Variant = "success" | "warning" | "danger" | "info" | "muted";

const styles: Record<Variant, string> = {
  success: "bg-success/10 text-success",
  warning: "bg-warning/10 text-warning",
  danger: "bg-danger/10 text-danger",
  info: "bg-info/10 text-info",
  muted: "bg-ink/5 text-muted",
};

export function StatusBadge({
  children,
  variant = "muted",
  dot = false,
}: {
  children: React.ReactNode;
  variant?: Variant;
  dot?: boolean;
}) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-bold ${styles[variant]}`}
    >
      {dot && <span className="h-1.5 w-1.5 rounded-full bg-current" />}
      {children}
    </span>
  );
}

export interface Column<T> {
  header: string;
  cell: (row: T) => React.ReactNode;
  align?: "start" | "end" | "center";
  className?: string;
}

export function DataTable<T>({
  columns,
  rows,
}: {
  columns: Column<T>[];
  rows: T[];
}) {
  return (
    <div className="overflow-x-auto rounded-xl border border-border bg-surface shadow-soft">
      <table className="w-full border-collapse text-sm">
        <thead>
          <tr className="bg-surface-alt">
            {columns.map((c, i) => (
              <th
                key={i}
                className={`px-3 py-2.5 text-xs font-extrabold uppercase tracking-wide text-muted whitespace-nowrap ${
                  c.align === "end"
                    ? "text-end"
                    : c.align === "center"
                    ? "text-center"
                    : "text-start"
                }`}
              >
                {c.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r, ri) => (
            <tr
              key={ri}
              className="border-t border-border transition-colors hover:bg-ink/[0.03]"
            >
              {columns.map((c, ci) => (
                <td
                  key={ci}
                  className={`px-3 py-2.5 text-ink/90 ${
                    c.align === "end"
                      ? "text-end tabular-nums"
                      : c.align === "center"
                      ? "text-center"
                      : "text-start"
                  } ${c.className ?? ""}`}
                >
                  {c.cell(r)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

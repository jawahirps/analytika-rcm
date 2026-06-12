import { CloudDownload, Inbox } from "lucide-react";
import { DataTable, type Column } from "../../components/ui/DataTable";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatusBadge } from "../../components/ui/StatusBadge";
import { FilterBar } from "../../components/ui/FilterBar";
import { portalFiles } from "../../mock/portal";
import type { PortalFileRow } from "../../mock/types";

export function PortalFiles() {
  const downloaded = portalFiles.filter((f) => f.downloaded).length;
  const pending = portalFiles.length - downloaded;

  const columns: Column<PortalFileRow>[] = [
    { header: "Facility", cell: (r) => <span className="font-semibold text-ink">{r.facility}</span> },
    { header: "File", cell: (r) => <span className="font-mono text-xs text-muted">{r.fileName}</span> },
    { header: "Type", cell: (r) => <StatusBadge variant="info">{r.type}</StatusBadge> },
    { header: "Direction", cell: (r) => <span className="text-muted">{r.direction}</span> },
    { header: "Synced", cell: (r) => <span className="text-muted">{r.syncedAt}</span> },
    {
      header: "Status",
      align: "center",
      cell: (r) =>
        r.downloaded ? (
          <StatusBadge variant="success" dot>Downloaded</StatusBadge>
        ) : (
          <StatusBadge variant="warning" dot>Pending</StatusBadge>
        ),
    },
  ];

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-extrabold text-ink">
            <Inbox size={20} className="text-teal" /> Portal Files
          </h1>
          <p className="text-sm text-muted">
            Raw file inventory pulled from the DHA / RHA portal ·{" "}
            <span className="font-semibold text-success">{downloaded} downloaded</span> ·{" "}
            <span className="font-semibold text-gold">{pending} pending</span>
          </p>
        </div>
        {pending > 0 && (
          <button className="inline-flex items-center gap-2 rounded-lg bg-gold px-3 py-2 text-sm font-bold text-white">
            <CloudDownload size={15} /> Download {pending} pending
          </button>
        )}
      </div>

      <FilterBar />
      <SectionCard title={`${portalFiles.length} files`}>
        <DataTable columns={columns} rows={portalFiles} />
      </SectionCard>
    </div>
  );
}

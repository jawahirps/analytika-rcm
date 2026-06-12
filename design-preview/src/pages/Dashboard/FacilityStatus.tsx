import { CircleCheck, CircleX, Database, FileDown, Files, TriangleAlert } from "lucide-react";
import { DataTable, type Column } from "../../components/ui/DataTable";
import { ProgressBar } from "../../components/ui/ProgressBar";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatusBadge } from "../../components/ui/StatusBadge";
import { facilityStatus } from "../../mock/facilityStatus";
import type { FacilityStatusRow } from "../../mock/types";

const statusVariant = {
  Connected: "success",
  Degraded: "warning",
  Disconnected: "danger",
} as const;

function Tile({
  icon,
  value,
  label,
  color,
}: {
  icon: React.ReactNode;
  value: string;
  label: string;
  color: string;
}) {
  return (
    <div className="rounded-xl border border-border bg-surface p-4 shadow-soft">
      <span
        className="grid h-10 w-10 place-items-center rounded-lg"
        style={{ background: `color-mix(in srgb, ${color} 14%, transparent)`, color }}
      >
        {icon}
      </span>
      <div className="mt-3 text-2xl font-extrabold tabular-nums text-ink">{value}</div>
      <div className="text-xs font-semibold uppercase tracking-wide text-muted">{label}</div>
    </div>
  );
}

export function FacilityStatus() {
  const vm = facilityStatus;
  const columns: Column<FacilityStatusRow>[] = [
    { header: "Facility", cell: (r) => <span className="font-semibold text-ink">{r.facilityName}</span> },
    {
      header: "Portal",
      cell: (r) => <StatusBadge variant="info">{r.portal}</StatusBadge>,
    },
    {
      header: "Status",
      cell: (r) => (
        <StatusBadge dot variant={statusVariant[r.status]}>
          {r.status}
        </StatusBadge>
      ),
    },
    { header: "Last Sync", cell: (r) => <span className="text-muted">{r.lastSyncTime}</span> },
    { header: "Records", align: "end", cell: (r) => r.recordCount.toLocaleString() },
    { header: "Claims", align: "end", cell: (r) => r.claimCount.toLocaleString() },
    {
      header: "Files Downloaded",
      className: "min-w-[180px]",
      cell: (r) => (
        <div>
          <div className="mb-1 text-xs text-muted">
            {r.downloadedFilesCount.toLocaleString()} / {r.fileCount.toLocaleString()}
          </div>
          <ProgressBar
            value={r.downloadedFilesCount}
            max={Math.max(1, r.fileCount)}
            color={r.pendingFilesCount > 0 ? "var(--gold)" : "var(--mint)"}
          />
        </div>
      ),
    },
  ];

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-xl font-extrabold text-ink">Facility Status</h1>
        <p className="text-sm text-muted">
          Live connectivity and sync health across registered facilities · last sync {vm.lastSyncTime}
        </p>
      </div>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
        <Tile icon={<CircleCheck size={20} />} value={String(vm.connectedCount)} label="Connected" color="var(--success)" />
        <Tile icon={<TriangleAlert size={20} />} value={String(vm.degradedCount)} label="Degraded" color="var(--gold)" />
        <Tile icon={<CircleX size={20} />} value={String(vm.disconnectedCount)} label="Disconnected" color="var(--coral)" />
        <Tile icon={<Database size={20} />} value={vm.totalRecords.toLocaleString()} label="Total Records" color="var(--teal)" />
        <Tile icon={<Files size={20} />} value={vm.totalFiles.toLocaleString()} label="Total Files" color="var(--forest)" />
      </div>

      <SectionCard
        title="Facilities"
        subtitle={`${vm.facilities.length} registered`}
        right={
          <span className="inline-flex items-center gap-1.5 text-xs font-semibold text-muted">
            <FileDown size={14} /> {vm.totalClaimCount.toLocaleString()} claims
          </span>
        }
      >
        <DataTable columns={columns} rows={vm.facilities} />
      </SectionCard>
    </div>
  );
}

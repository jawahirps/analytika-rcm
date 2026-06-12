import { FileBarChart } from "lucide-react";
import { DataTable, type Column } from "../../components/ui/DataTable";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatusBadge } from "../../components/ui/StatusBadge";
import { FilterBar } from "../../components/ui/FilterBar";
import { workload } from "../../mock/resubmission";
import type { WorkloadRow } from "../../mock/types";

const priorityVariant = { High: "danger", Medium: "warning", Low: "muted" } as const;
const statusVariant = {
  Open: "info",
  "In Review": "warning",
  Resubmitted: "success",
  Closed: "muted",
} as const;

export function Workload() {
  const columns: Column<WorkloadRow>[] = [
    { header: "Claim ID", cell: (r) => <span className="font-mono text-xs">{r.claimId}</span> },
    { header: "Facility", cell: (r) => <span className="font-semibold text-ink">{r.facility}</span> },
    { header: "Analyst", cell: (r) => <span className="text-muted">{r.analyst}</span> },
    {
      header: "Priority",
      cell: (r) => <StatusBadge variant={priorityVariant[r.priority]}>{r.priority}</StatusBadge>,
    },
    {
      header: "Status",
      cell: (r) => <StatusBadge variant={statusVariant[r.status]} dot>{r.status}</StatusBadge>,
    },
    { header: "Age", align: "end", cell: (r) => `${r.ageDays}d` },
    { header: "Amount (AED)", align: "end", cell: (r) => r.amount.toLocaleString() },
  ];

  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-xl font-extrabold text-ink">
          <FileBarChart size={20} className="text-teal" /> Workload
        </h1>
        <p className="text-sm text-muted">Analyst resubmission queue (synthetic).</p>
      </div>
      <FilterBar options={["All analysts", "S. Khan — Demo", "M. Ali — Demo", "R. Das — Demo"]} />
      <SectionCard title={`${workload.length} claims in queue`}>
        <DataTable columns={columns} rows={workload} />
      </SectionCard>
    </div>
  );
}

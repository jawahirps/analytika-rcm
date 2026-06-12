import { FileText, Link2 } from "lucide-react";
import { DataTable, type Column } from "../../components/ui/DataTable";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatusBadge } from "../../components/ui/StatusBadge";
import { FilterBar } from "../../components/ui/FilterBar";
import { claimExtracts } from "../../mock/portal";
import type { ClaimExtractRow } from "../../mock/types";

export function ClaimExtracts() {
  const matched = claimExtracts.filter((c) => c.matched).length;

  const columns: Column<ClaimExtractRow>[] = [
    { header: "Facility", cell: (r) => <span className="font-semibold text-ink">{r.facility}</span> },
    { header: "Claim ID", cell: (r) => <span className="font-mono text-xs">{r.claimId}</span> },
    {
      header: "Kind",
      cell: (r) => (
        <StatusBadge variant={r.kind === "Remittance" ? "info" : "muted"}>{r.kind}</StatusBadge>
      ),
    },
    { header: "Payer", cell: (r) => <span className="text-muted">{r.payer}</span> },
    {
      header: "Net (AED)",
      align: "end",
      cell: (r) => r.netAmount.toLocaleString(),
    },
    {
      header: "Match",
      align: "center",
      cell: (r) =>
        r.matched ? (
          <StatusBadge variant="success" dot>Matched</StatusBadge>
        ) : (
          <StatusBadge variant="warning" dot>Unmatched</StatusBadge>
        ),
    },
  ];

  return (
    <div className="space-y-4">
      <div>
        <h1 className="flex items-center gap-2 text-xl font-extrabold text-ink">
          <FileText size={20} className="text-forest" /> Claim Extracts
        </h1>
        <p className="text-sm text-muted">
          Parsed claim &amp; remittance rows extracted from portal files ·{" "}
          <span className="inline-flex items-center gap-1 font-semibold text-success">
            <Link2 size={13} /> {matched}/{claimExtracts.length} matched
          </span>
        </p>
      </div>

      <FilterBar />
      <SectionCard title={`${claimExtracts.length} parsed rows`}>
        <DataTable columns={columns} rows={claimExtracts} />
      </SectionCard>
    </div>
  );
}

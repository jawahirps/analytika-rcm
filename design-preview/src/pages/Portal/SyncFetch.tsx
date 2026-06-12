import { Building2, Play, RefreshCw } from "lucide-react";
import { ProgressBar } from "../../components/ui/ProgressBar";
import { SectionCard } from "../../components/ui/SectionCard";
import { StatusBadge } from "../../components/ui/StatusBadge";
import { facilityStatus } from "../../mock/facilityStatus";

export function SyncFetch() {
  const rows = facilityStatus.facilities.filter((f) => f.status !== "Disconnected");
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-extrabold text-ink">
            <RefreshCw size={20} className="text-teal" /> Sync &amp; Fetch
          </h1>
          <p className="text-sm text-muted">
            Pull submitted &amp; remittance files from each facility portal.
          </p>
        </div>
        <button className="inline-flex items-center gap-2 rounded-lg bg-navy px-3 py-2 text-sm font-bold text-white">
          <Play size={15} /> Sync all facilities
        </button>
      </div>

      <div className="grid gap-3 md:grid-cols-2">
        {rows.map((f) => {
          const total = Math.max(1, f.fileCount);
          const done = f.downloadedFilesCount;
          return (
            <SectionCard key={f.facilityName}>
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  <span className="grid h-9 w-9 place-items-center rounded-lg bg-teal/12 text-teal">
                    <Building2 size={17} />
                  </span>
                  <div>
                    <div className="text-sm font-bold text-ink">{f.facilityName}</div>
                    <div className="text-xs text-muted">{f.portal} · last {f.lastSyncTime}</div>
                  </div>
                </div>
                <StatusBadge
                  dot
                  variant={f.status === "Connected" ? "success" : "warning"}
                >
                  {f.status}
                </StatusBadge>
              </div>
              <div className="mt-3">
                <div className="mb-1 flex justify-between text-xs text-muted">
                  <span>{done.toLocaleString()} / {f.fileCount.toLocaleString()} files</span>
                  <span>{f.pendingFilesCount} pending</span>
                </div>
                <ProgressBar value={done} max={total} color={f.pendingFilesCount ? "var(--gold)" : "var(--mint)"} />
              </div>
            </SectionCard>
          );
        })}
      </div>
    </div>
  );
}

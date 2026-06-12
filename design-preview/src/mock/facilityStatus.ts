// SYNTHETIC PREVIEW DATA — NO PHI. Fake facility names & counts.
import type { FacilityStatusRow, FacilityStatusVM } from "./types";

const rows: FacilityStatusRow[] = [
  { facilityName: "Cedar Medical Center — Demo", portal: "DHA", status: "Connected", lastSyncTime: "10 Jun 2026 12:15", recordCount: 44266, claimCount: 21840, fileCount: 1820, downloadedFilesCount: 1820, pendingFilesCount: 0 },
  { facilityName: "Al Noor Clinic — Demo", portal: "DHA", status: "Connected", lastSyncTime: "10 Jun 2026 12:15", recordCount: 38112, claimCount: 18203, fileCount: 1554, downloadedFilesCount: 1500, pendingFilesCount: 54 },
  { facilityName: "Palm Care Hospital — Demo", portal: "Both", status: "Connected", lastSyncTime: "10 Jun 2026 12:27", recordCount: 55241, claimCount: 27110, fileCount: 2204, downloadedFilesCount: 2204, pendingFilesCount: 0 },
  { facilityName: "Marina Health — Demo", portal: "DHA", status: "Degraded", lastSyncTime: "09 Jun 2026 22:04", recordCount: 56055, claimCount: 26500, fileCount: 2310, downloadedFilesCount: 1980, pendingFilesCount: 330 },
  { facilityName: "Lotus Polyclinic — Demo", portal: "RHA", status: "Connected", lastSyncTime: "10 Jun 2026 12:38", recordCount: 19884, claimCount: 9120, fileCount: 905, downloadedFilesCount: 905, pendingFilesCount: 0 },
  { facilityName: "Apollo Day Surgery — Demo", portal: "DHA", status: "Degraded", lastSyncTime: "08 Jun 2026 03:11", recordCount: 37359, claimCount: 17800, fileCount: 1600, downloadedFilesCount: 1240, pendingFilesCount: 360 },
  { facilityName: "Sunrise Dental — Demo", portal: "DHA", status: "Connected", lastSyncTime: "10 Jun 2026 12:53", recordCount: 8044, claimCount: 3900, fileCount: 410, downloadedFilesCount: 410, pendingFilesCount: 0 },
  { facilityName: "Harbor Family Clinic — Demo", portal: "RHA", status: "Disconnected", lastSyncTime: "—", recordCount: 0, claimCount: 0, fileCount: 0, downloadedFilesCount: 0, pendingFilesCount: 0 },
  { facilityName: "Greenfield Medical — Demo", portal: "DHA", status: "Connected", lastSyncTime: "10 Jun 2026 12:55", recordCount: 29671, claimCount: 14010, fileCount: 1190, downloadedFilesCount: 1190, pendingFilesCount: 0 },
  { facilityName: "Bluewave Hospital — Demo", portal: "Both", status: "Disconnected", lastSyncTime: "—", recordCount: 0, claimCount: 0, fileCount: 0, downloadedFilesCount: 0, pendingFilesCount: 0 },
];

export const facilityStatus: FacilityStatusVM = {
  facilities: rows,
  connectedCount: rows.filter((r) => r.status === "Connected").length,
  degradedCount: rows.filter((r) => r.status === "Degraded").length,
  disconnectedCount: rows.filter((r) => r.status === "Disconnected").length,
  totalRecords: rows.reduce((s, r) => s + r.recordCount, 0),
  totalClaimCount: rows.reduce((s, r) => s + r.claimCount, 0),
  totalFiles: rows.reduce((s, r) => s + r.fileCount, 0),
  lastSyncTime: "10 Jun 2026 12:55",
};

// SYNTHETIC PREVIEW DATA — NO PHI. Fake file names, claim ids, payers.
import type { ClaimExtractRow, PortalFileRow } from "./types";

const facilities = [
  "Cedar Medical — Demo",
  "Al Noor Clinic — Demo",
  "Palm Care — Demo",
  "Marina Health — Demo",
  "Lotus Polyclinic — Demo",
];
const payers = ["Daman Demo", "Thiqa Demo", "NextCare Demo", "Neuron Demo", "Aafiya Demo"];

export const portalFiles: PortalFileRow[] = Array.from({ length: 28 }).map((_, i) => {
  const types = ["Claim", "Remittance", "Prior Request", "Prior Auth"] as const;
  const type = types[i % types.length];
  return {
    facility: facilities[i % facilities.length],
    fileName: `DEMO_${type.replace(" ", "")}_2026-06-${String((i % 28) + 1).padStart(2, "0")}_${1000 + i}.xml`,
    type,
    direction: type === "Remittance" || type === "Prior Auth" ? "Received" : "Sent",
    syncedAt: `10 Jun 2026 ${String(8 + (i % 10)).padStart(2, "0")}:${String((i * 7) % 60).padStart(2, "0")}`,
    downloaded: i % 5 !== 0,
  };
});

export const claimExtracts: ClaimExtractRow[] = Array.from({ length: 26 }).map((_, i) => {
  const kind = i % 3 === 0 ? "Remittance" : "Submission";
  return {
    facility: facilities[i % facilities.length],
    claimId: `DEMO-CLM-${String(40010 + i)}`,
    kind,
    payer: payers[i % payers.length],
    netAmount: Math.round(400 + Math.abs(Math.sin(i)) * 5200),
    matched: i % 4 !== 0,
  };
});

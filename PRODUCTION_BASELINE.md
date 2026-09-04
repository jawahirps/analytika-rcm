# Bix production baseline

The canonical production source is the `production-final` branch. Immutable releases use signed-off annotated tags named `vYYYY.MM.DD.N`.

## Release gates

1. The full solution must build in Release mode and every automated test must pass.
2. Publish with `SourceRevisionId` set to the exact commit being deployed.
3. Never overwrite or relocate `DB_DIR`, the SQLite database, or data-protection keys during deployment.
4. Run schema upgrades before accepting traffic. Schema changes must be additive and backward compatible.
5. Verify `/healthz`, `/api/health`, and an authenticated report page after restart.
6. `/api/health` must report the deployed commit, never `unknown` or a different revision.
7. Report filters must fail closed on invalid dates or unauthorized facilities. Do not silently substitute dates, facilities, formats, or incomplete parsed data.
8. Data grids may paginate for responsiveness, but exports and totals must operate on the complete filtered result set and disclose any intentional limit.

## Current baseline

- Application: Bix Analytika RCM
- Export format: XLSX
- Renderer: WebGPU with a non-animated CSS background when WebGPU is unavailable; no WebGL fallback
- Local listeners: ports 5000 and 5200
- Public endpoint: `https://bix.ghafservices.com`

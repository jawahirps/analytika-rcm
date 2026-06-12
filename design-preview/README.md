# Analytika RCM — UI/UX Design Preview

A **standalone prototype** of a redesigned Analytika RCM front end, built to
preview a full UX direction before investing in porting it to the production
Razor app.

> ⚠️ **This is a mockup.** It is **not** wired into the production ASP.NET Core
> app, shares no build with it, and uses **synthetic data only — no PHI**. Every
> mock file under `src/mock/` carries fake facility names, claim IDs and payers.

## Stack
- **Vite + React 18 + TypeScript + Tailwind CSS**
- **recharts** (area/donut charts), **lucide-react** (icons), **react-router-dom**
- Ghaf brand tokens (navy / teal / gold) translated from the production
  `Analytika/wwwroot/css/site.css` into `src/theme/tokens.css`
- Full **dark mode** (class-based) and **EN/AR RTL** toggle, persisted to `localStorage`

## Screens (top-screens scope)
| Route | Screen |
|---|---|
| `/dashboard` | Facility Status (connectivity + sync health) |
| `/reports/:tab` | BI Reports — 8 accent-themed tabs (Submissions … Department) |
| `/portal/sync` | Sync & Fetch |
| `/portal/files` | Portal Files (raw portal inbox) |
| `/portal/extracts` | Claim Extracts (parsed claim/remittance rows) |
| `/resubmission/denials` | Denial Dashboard |
| `/resubmission/workload` | Analyst Workload queue |

## Run it
```bash
cd design-preview
npm install
npm run dev      # → http://localhost:5180
```
Build a static bundle: `npm run build` then `npm run preview`.

## Relationship to production
- Data shapes in `src/mock/types.ts` are **hand-mirrored** from the real
  `Analytika/Models/ViewModels/*` so approved designs can be ported to Razor
  later — but there is **zero compile-time coupling**.
- Brand palette and the 8 BI tab accents are copied from the live CSS /
  `RCMDashboard.cshtml`; if production tokens change, resync `tokens.css`.
- Nothing here is referenced by `Analytika.sln` or the `.csproj`.

# Ghaf BIX — Demonstration Website

A self-contained, single-file marketing site (`index.html`) presenting **Ghaf BIX**
(Ghaf Business Intelligence eXperience) as a SaaS offering. No build step, no
external dependencies — open `index.html` directly in any browser or serve the
folder statically.

## Sections

1. **Hero** — value proposition with an animated facility-dashboard mock fed by
   real product numbers (13 facilities, 67,523 records, 47,159 files).
2. **Product** — six feature cards mapped to the actual platform modules:
   portal sync, XML parsing, reconciliation, reports, resubmission, admin/security.
3. **Architecture** — the five-layer pipeline behind the product
   (Data Sources → Sync & Ingestion → Core Platform → Intelligence → Delivery),
   grounded in the real stack: ASP.NET Core, EF Core, Hangfire, Identity,
   Docker / Railway / Render / Windows-service deployments.
4. **SaaS Roadmap** — three-phase evolution: dedicated instances → managed
   cloud → multi-tenant SaaS.
5. **Pricing** — illustrative per-facility tiers (Clinic / Group / Enterprise).
6. **Projection** — animated SVG chart of 3-year ARR & facility growth plus a
   TAM/SAM/SOM funnel and unit-economics assumptions.

> All commercial figures (pricing, ARR, TAM/SAM/SOM, margins) are **illustrative
> planning numbers for demonstration purposes only** — adjust them in the
> `Projection` section data and the pricing cards as the model firms up.

## Quick start

```bash
# open directly
open artifacts/ghaf-bix-site/index.html

# or serve it
python3 -m http.server -d artifacts/ghaf-bix-site 8000
```

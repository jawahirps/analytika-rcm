# Ghaf BIX — Demonstration Website

A self-contained marketing site (`index.html`) for **Ghaf BIX** (Ghaf Business
Intelligence). No build step — open `index.html` directly in any browser or
serve the folder statically.

## Logo assets

| File | Use |
|---|---|
| `logo-mark.svg` | Square app icon / favicon — Ghaf tree with data-node canopy on a navy→teal tile |
| `logo.svg` | Horizontal lockup — mark + "Ghaf BIX" wordmark |
| `logo-mark-512.png` | 512×512 PNG export (app icon, social avatars) |
| `logo-1440.png` | Wide PNG export of the lockup (docs, decks, email signatures) |

The mark follows the existing in-app login motif (Ghaf tree whose branches are
data streams ending in data points) and the brand palette (`#003B4D` navy,
`#006884`/`#008B8B` teal, `#54ACBF`/`#A7EBF2` accents). Drop the SVGs into
`Analytika/wwwroot/images/` if you want to use them inside the app itself.

## Site sections

1. **Hero** — value proposition with an animated facility-dashboard mock fed by
   real product numbers (13 facilities, 67,523 records, 47,159 files).
2. **Product** — six feature cards mapped to the platform modules: portal sync,
   XML parsing, reconciliation, reports, resubmission, admin/security.
3. **Platform** — the five-layer pipeline behind the product, framed around
   reliability (resumable syncs, stall detection) and deployment choice
   (managed cloud or on-premise).
4. **Getting Started** — Connect → Backfill → Operate, three steps to live.
5. **Outcomes** — what changes for the team once the pipeline runs itself.
6. **Pricing** — per-facility tiers (Clinic / Group / Enterprise).

## Quick start

```bash
# open directly
open artifacts/ghaf-bix-site/index.html

# or serve it
python3 -m http.server -d artifacts/ghaf-bix-site 8000
```

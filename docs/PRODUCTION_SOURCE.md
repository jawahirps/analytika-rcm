# Bix production source of truth

## Canonical repository

- Repository: `jawahirps/analytika-rcm`
- Production branch: `main`
- Public URL: `https://bix.ghafservices.com`
- Runtime: ASP.NET Core on the Ghaf office Windows server
- Service: `GhafBI`, listening on `http://localhost:5200`
- Edge routing: Cloudflare Tunnel service `GhafBITunnel`

The repository `jawahirps/BIx-powered-by-ghafservices` is an AI Studio React
prototype. It is not the code currently serving the production domain and must
not be used for production deployment.

## Deployment identity

`deploy/1_publish.ps1` embeds the source commit into the published assembly and
writes `deployment-manifest.json`. The running deployment exposes the identity
at `GET /api/health`.

After every deployment, verify that `commitSha` from `/api/health` equals the
commit being released. A deployment is not complete until those values match.

`GET /healthz` remains the deep readiness check for database and portal-sync
health. Supervisors and uptime checks use `/api/health`, because a dependency
outage must not cause the healthy web process to restart repeatedly.


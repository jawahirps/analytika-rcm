# Cloudflare Tunnel for Docker Deployments

This repository already includes two deployment paths:

- `docker-compose.yml` to run the Analytika app in containers
- `deploy/3_cloudflared_config.yml` for a Windows service install on a server

This file adds the missing container-side tunnel setup so Docker deployments can
sit behind a named Cloudflare Tunnel without exposing inbound ports directly.

## Prerequisites

1. Create or choose a named Cloudflare Tunnel in Zero Trust.
2. Copy the tunnel token for that tunnel.
3. Copy `.env.cloudflared.example` to `.env.cloudflared`.
4. Replace `CLOUDFLARE_TUNNEL_TOKEN` with the real token.

## Start the app with Cloudflare Tunnel

Run from the repository root:

```powershell
docker compose --env-file .env.cloudflared -f docker-compose.yml -f docker-compose.cloudflared.yml up -d
```

This starts:

- `analytika` from `docker-compose.yml`
- `cloudflared` from `docker-compose.cloudflared.yml`

## Recommended Cloudflare ingress target

In the Cloudflare dashboard, point the public hostname for the named tunnel to:

```text
http://analytika:8080
```

That hostname works because both services share the same Docker Compose network.

## Current connector reference

Current Cloudflare connector ID reference:

```text
afb0aa68-25fc-4c70-9ace-427caf0abbba
```

This is useful for operator tracking, but it is not the same thing as the named
tunnel ID used in `deploy/3_cloudflared_config.yml`.

## Verify

1. `docker compose ps`
2. `docker compose logs cloudflared --tail 100`
3. Open the public hostname configured on the tunnel.

## Windows service path

If you are deploying directly onto a Windows server instead of Docker, keep
using `deploy/2_install_service.ps1` and `deploy/3_cloudflared_config.yml`.

## Optional Worker layer

If you want a Cloudflare Worker in front of the tunnel, use the project in
`cloudflare-worker/` and follow `deploy/CLOUDFLARE_WORKER.md`.

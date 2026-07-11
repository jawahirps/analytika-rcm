# Cloudflare Worker Proxy

This repository now includes a Worker project at
`cloudflare-worker/` for cases where you want Cloudflare logic in front of the
existing Analytika tunnel instead of routing users straight to the tunnel
hostname.

## Use case

Use the Worker when you want to:

- attach a custom Worker route or custom domain
- add headers, auth logic, rate limits, or logging later
- keep the tunnel hostname hidden from end users

## Files

- `cloudflare-worker/wrangler.toml`
- `cloudflare-worker/src/index.js`
- `cloudflare-worker/.dev.vars.example`

## Required variable

Set `ANALYTIKA_TUNNEL_ORIGIN` to the tunnel target the Worker should proxy to.

Examples:

- `https://my-named-tunnel.example.com`
- `https://random-name.trycloudflare.com`

## Deploy steps

```powershell
cd cloudflare-worker
npm install
npx wrangler secret put ANALYTIKA_TUNNEL_ORIGIN
npx wrangler deploy
```

When prompted for the secret value, paste the tunnel origin URL.

## Route safely

Make sure the Worker route hostname is different from the hostname stored in
`ANALYTIKA_TUNNEL_ORIGIN`. Otherwise the Worker will proxy back to itself.

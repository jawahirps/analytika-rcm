# Analytika Cloudflare Worker

This Worker sits in front of the Analytika tunnel and forwards requests to the
same origin configured for `cloudflared`.

## What it does

- accepts requests on a Worker route or `workers.dev`
- proxies them to `ANALYTIKA_TUNNEL_ORIGIN`
- preserves path and query string
- forwards common proxy headers
- adds a few baseline response headers

## Local setup

1. From this folder run `npm install`
2. Copy `.dev.vars.example` to `.dev.vars`
3. Set `ANALYTIKA_TUNNEL_ORIGIN` to the tunnel URL or named tunnel hostname
4. Run `npm run dev`

## Deploy

1. Create a Worker secret or plain env var named `ANALYTIKA_TUNNEL_ORIGIN`
2. Set it to your tunnel origin, for example:
   `https://analytika-tunnel.example.com`
3. Deploy with `npm run deploy`

## Important routing note

Do not point `ANALYTIKA_TUNNEL_ORIGIN` back to the same hostname that is routed
to this Worker, or the Worker will call itself and loop.

Use one of these instead:

- the `*.trycloudflare.com` URL while testing
- a named tunnel hostname that is not routed to this Worker
- a private Cloudflare Tunnel hostname used only as the Worker origin

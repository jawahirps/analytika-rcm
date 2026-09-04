# Job 1: Runtime Health Agent

## Scope
- Verify `BixApp`, localhost `:5000`, Cloudflare tunnel, `bix.ghafservices.com`, `/healthz`, logs, and degraded causes

## Deliverable
- Clear UP/DOWN report
- Fixes for service startup, health checks, stale sync, or tunnel routing

## Acceptance
- Local app returns 200 on `/healthz`
- Public URL `bix.ghafservices.com` works
- Health status explains real failure causes

## Current Status
- App runs on `http://localhost:5200`
- `/healthz` returns `Degraded` (expected - optional features off by default)
- SMTP, Hangfire, OpenTelemetry are optional and off/unconfigured by default
- Non-fatal warnings during startup

## Fixes Needed
- Investigate why `/healthz` shows Degraded vs Healthy
- Ensure core app health is UP despite optional feature warnings
- Verify Cloudflare tunnel routing to `bix.ghafservices.com`
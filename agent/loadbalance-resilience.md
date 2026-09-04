# Job 3: Load-Balance & Error Resilience Agent

## Scope
- Investigate intermittent errors, timeout handling, retry logic
- Worker/service conflicts, connection pooling, background jobs
- Cloudflare behavior and edge cases

## Deliverable
- Hardened request flow
- Graceful fallbacks
- Better logging
- Timeout/retry policies

## Acceptance
- No generic "Something went wrong" for known failures
- Actionable errors are logged and shown to users
- Retry logic works for transient failures
- Connection pooling configured properly

## Areas to Investigate
- Cloudflare tunnel stability
- Background job (Hangfire) reliability
- API timeout configurations
- Error handling middleware
- Retry policies for external calls
- Connection string pooling settings
# 0016. Role of Redis

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
Redis is fast, but not durable in the way money needs. The original application refused to start without it.

## Decision
Redis is an **accelerator, never the source of truth**:
| Use | Key / TTL | If Redis is unavailable |
|---|---|---|
| Frame replay cache (online path) | `replay:{tid}:{ts}` · 60 s | Fall back to `UNIQUE (ticket_id, frame_ts)` on the taps table. Correct, slightly slower |
| Device request nonces | `nonce:{device}:{nonce}` · 5 min | Fall back to a PostgreSQL nonce table with TTL purge |
| Distributed rate-limit counters | `rl:{policy}:{key}` | Fall back to the per-instance in-memory limiter (degraded but bounded) |

The connection uses `AbortOnConnectFail = false`, so the app starts and reports **Degraded** on readiness instead of crashing.

## Alternatives considered
- **Redis for idempotency or balances.** Rejected (ADR 0006, ADR 0005).
- **No Redis.** Viable for the MVP using only the PostgreSQL fallbacks. Kept because it removes write load from the
  database on the hottest path (every scan), and its role is clearly bounded.

## Consequences
- Every Redis call path has a tested fallback. An integration test runs the tap flow with Redis stopped.

# 0006. Idempotency for commands, webhooks and taps

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
Networks retry. Mobile clients double-tap. Gateways redeliver webhooks. Gates resend taps after reconnecting. The
original system turned each retry into a new effect: replayed top-ups, double-charged purchases (PRD P1).

## Decision
There are three mechanisms, one per source of duplicates.

**1. Client commands: `Idempotency-Key` header** (required on purchase, top-up intent, cancel).
```
idempotency_records(user_id, key, request_hash, status, response_code, response_body, locked_until, created_at)
PRIMARY KEY (user_id, key)
```
1. `INSERT … ON CONFLICT DO NOTHING` with `status = InProgress`, `locked_until = now + 30s`. This is its own short transaction, so concurrent duplicates see it immediately.
2. If the insert succeeds, run the handler. The business effect **and** the update of the record to `Completed` with the
   serialized response commit in **one** transaction.
3. On conflict:
   - Same `request_hash` and `Completed` → replay the stored response.
   - `InProgress` and not expired → `409 Conflict` with `Retry-After`.
   - Different `request_hash` → `422` (key reused for a different request).
   - `InProgress` but `locked_until` passed → the earlier attempt crashed before committing. Its effect rolled back with
     its transaction, so it is safe to reclaim the record and run the handler.

Records live for 24 hours. A sweeper job deletes older ones.

**2. Webhooks.** Authenticity comes first. The gateway signs `timestamp + "." + raw_body` with HMAC-SHA256 under a shared
webhook secret, and sends `X-Signature: t=<ts>,v1=<hex>`. We verify over the **raw** bytes (before JSON parsing) with a
constant-time comparison, reject timestamps older than 5 minutes, and accept two active secrets during rotation.
The amount and currency in the event must match the stored intent, or the event is rejected and alerted.
Deduplication then uses `UNIQUE (provider, provider_event_id)` on an inbox table, inserted in the same transaction as the effect.
A duplicate violates the constraint, so the handler returns `200` and does nothing. The provider sees success and stops retrying.

**3. Taps:** the gate generates the tap ID (UUIDv7) when it validates the frame. `UNIQUE (tap_id)` makes sync after an outage
idempotent no matter how many times the GateAgent resends.

## Alternatives considered
- **Redis-only idempotency.** It is fast, but eviction or a failover forgets keys and the duplicate goes through. The
  guarantee has to be durable, so it lives in PostgreSQL in the same transaction as the effect.
- **Natural-key dedupe only (e.g. "one pending purchase per user").** Blocks legitimate concurrent purchases and does not
  generalize.

## Consequences
- Clients must send a fresh UUID per logical action and reuse it on retry. The PWA does this automatically.
- Stored responses must not contain secrets beyond what the original caller already received.

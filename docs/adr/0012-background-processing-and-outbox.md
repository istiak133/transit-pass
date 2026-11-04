# 0012. Background processing and transactional outbox

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
Several things must happen later, reliably, and exactly once in effect: expire payment intents and tickets, refund
expired tickets, close abandoned journeys, purge idempotency records, and publish domain events (notifications,
read models). The original system had no background processing (PRD P7). The API may run as several instances.

## Decision

**Sweeper jobs.** Each job is a `BackgroundService` that runs on an interval and claims work with:
```sql
SELECT id FROM ticketing.tickets
WHERE  status = 'Active' AND expires_at < now()
ORDER  BY expires_at
LIMIT  100
FOR UPDATE SKIP LOCKED;
```
Each batch is its own transaction, so a crash loses at most one uncommitted batch, which is retried next run. `SKIP LOCKED`
lets any number of instances share the work without coordination. Transitions are state-guarded, so a row that changed
in between is simply skipped.

**Transactional outbox.** A handler that changes state and needs to announce it inserts an `outbox_messages` row **in
the same transaction**. A dispatcher claims rows with `SKIP LOCKED`, publishes them to in-process handlers, and marks them
processed. Delivery is **at-least-once**. Consumers record processed message IDs in an `inbox` table to stay idempotent.
Failed messages back off exponentially, and after 10 attempts they are parked for inspection.

## Alternatives considered
- **Hangfire / Quartz.NET.** Mature, but they bring their own schema and dashboard for what are four simple sweeps.
  Quartz is the upgrade path once schedules become cron-like.
- **Publishing events directly after `SaveChanges`.** A crash between commit and publish loses the event. The dual-write problem is what the outbox exists to solve.
- **MassTransit / a broker now.** No out-of-process consumer exists yet. When a module is extracted, the dispatcher's
  in-process publish becomes a broker publish and nothing upstream changes.

## Consequences
- Every job is safe to run concurrently and safe to re-run.
- Outbox lag (oldest unprocessed message age) is a key metric and a readiness concern.

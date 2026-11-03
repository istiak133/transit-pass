# 0002. PostgreSQL as the system of record

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The core of the system is money and single-use credentials under concurrent access: two gates scanning the
same ticket, two purchases from one wallet, a webhook delivered twice. Correctness depends on the database's
locking and constraint primitives, not on application code.

## Decision
Use **PostgreSQL 17** with EF Core (Npgsql) for data access. The hot money paths use explicit, reviewed
SQL where EF's read-modify-write pattern would race.

The primitives we rely on:
- `UPDATE ... WHERE balance - @x >= @floor RETURNING` performs conditional atomic debits without application locks.
- `SELECT ... FOR UPDATE` serializes state transitions on a single ticket or journey row.
- `FOR UPDATE SKIP LOCKED` lets several app instances process background job queues without double work.
- Unique constraints are the final line of defence for idempotency (tap IDs, webhook event IDs, idempotency keys).
- The `xmin` system column serves as a free optimistic concurrency token for low-contention aggregates.

## Alternatives considered
- **SQL Server.** It is equally capable (`UPDLOCK, READPAST` is the analogue of `SKIP LOCKED`), and the original
  project used it. PostgreSQL won on cost (free managed tiers), container start time in Testcontainers (CI speed),
  and cleaner `SKIP LOCKED`/`RETURNING` semantics.
- **MongoDB.** Multi-document transactions exist but are not the natural model, and a double-entry ledger is
  relational by nature.

## Consequences
- Integration tests run against real PostgreSQL in containers. The EF in-memory provider is banned because it
  ignores transactions, constraints, and locking, which are exactly what we need to test.
- Redis is used only as an optimisation (replay cache, rate limits). PostgreSQL remains the source of truth, and the
  system fails closed if Redis is unavailable.

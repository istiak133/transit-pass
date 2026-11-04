# 0015. Data conventions

- **Status:** Accepted
- **Date:** 2026-11-04

## Decision
| Concern | Convention | Reason |
|---|---|---|
| Primary keys | **UUIDv7** (`Guid.CreateVersion7()`) generated in the application | Time-ordered, so B-tree inserts stay append-like. Not guessable, unlike sequential ints (IDOR). Known before insert, which lets handlers build related rows and outbox events without a round trip |
| Money | `bigint` minor units | ADR 0005 |
| Time | `timestamptz`, always UTC, from `TimeProvider` | One clock source for tests; no local-time bugs (the original crashed resolving a Windows timezone ID on Linux) |
| Naming | `snake_case` tables and columns (`EFCore.NamingConventions`) | Idiomatic PostgreSQL; no quoted identifiers in raw SQL |
| Schemas | One PostgreSQL schema per module (`identity`, `wallet`, `ticketing`, …) | Enforces ADR 0001 ownership at the database level |
| Migrations | EF Core migrations per module DbContext, applied by a **separate migration step** (bundle / CLI), not on app startup | Startup migrations race when several instances boot together, and they give the app DDL rights it should not need |
| Enums | Stored as text | Readable in SQL; safe to reorder in code |
| Soft delete | Not used | Status fields model lifecycles explicitly; ledger rows are immutable anyway |
| Concurrency | `xmin` token on low-contention aggregates; explicit locks on hot paths (ADRs 0005, 0010) | |
| Transient failures | Npgsql execution strategy retries `40001` (serialization) and `40P01` (deadlock) for idempotent units of work | |

## Consequences
- Every module has its own `DbContext` with `HasDefaultSchema`. Cross-module joins are impossible by construction.

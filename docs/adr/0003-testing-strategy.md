# 0003. Testing stack and strategy

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The risky behaviour is concurrency and failure handling: races on balances, duplicate deliveries, expiring
credentials. Example-based unit tests alone do not catch these.

## Decision
| Layer | Tooling | What it proves |
|---|---|---|
| Unit | xUnit v3, NSubstitute, Shouldly | Domain rules and state machines, with time injected via `TimeProvider` |
| Integration | `WebApplicationFactory` + Testcontainers (PostgreSQL, Redis) | Real SQL, constraints, transactions, HTTP contracts |
| Concurrency | Integration harness firing N parallel requests | Invariants hold under contention (e.g. N debits leave the exact balance) |
| Architecture | NetArchTest | Module boundaries from ADR 0001 |
| Load | k6 | Gate tap latency/throughput figures for the README |

Tests run on **Microsoft.Testing.Platform**, the default runner model for `dotnet test` on the .NET 10 SDK.
Coverage comes from `Microsoft.Testing.Extensions.CodeCoverage`.

## Alternatives considered
- **FluentAssertions.** From v8 it uses a commercial licence. Shouldly is MIT and reads just as well.
- **Moq.** Its 2023 SponsorLink incident damaged trust. NSubstitute has a terser API and no such history.
- **EF Core InMemory / SQLite for integration tests.** Neither enforces PostgreSQL locking semantics, so
  they cannot test our concurrency guarantees.

## Consequences
- Integration tests need Docker locally and in CI (available on `ubuntu-latest` runners).
- Time-dependent code must take `TimeProvider`; `DateTime.UtcNow` is banned outside the composition root.

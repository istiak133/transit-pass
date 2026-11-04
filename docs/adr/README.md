# Architecture Decision Records

Each significant decision is recorded as a short, immutable ADR ([Nygard format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)).
A decision is never edited after acceptance; it is superseded by a new ADR that links back.

| # | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith over microservices | Accepted |
| [0002](0002-postgresql.md) | PostgreSQL as the system of record | Accepted |
| [0003](0003-testing-strategy.md) | Testing stack and strategy | Accepted |
| [0004](0004-build-and-ci-conventions.md) | Build and CI conventions | Accepted |
| [0005](0005-money-and-double-entry-ledger.md) | Money representation and double-entry ledger | Accepted |
| [0006](0006-idempotency.md) | Idempotency for commands, webhooks and taps | Accepted |
| [0007](0007-rotating-device-bound-credentials.md) | Rotating, device-bound QR credentials | Accepted |
| [0008](0008-gate-device-authentication.md) | Gate device authentication | Accepted |
| [0009](0009-online-first-offline-fallback-gates.md) | Online-first, offline-fallback gates | Accepted |
| [0010](0010-journeys-and-exit-resolution.md) | Journey processing and exit resolution | Accepted |
| [0011](0011-identity-and-sessions.md) | Identity, passwords and sessions | Accepted |
| [0012](0012-background-processing-and-outbox.md) | Background processing and transactional outbox | Accepted |
| [0013](0013-network-and-fare-engine.md) | Network model and fare engine | Accepted |
| [0014](0014-api-style.md) | API style: Minimal APIs, vertical slices, problem details | Accepted |
| [0015](0015-data-conventions.md) | Data conventions | Accepted |
| [0016](0016-redis-role.md) | Role of Redis | Accepted |
| [0017](0017-observability.md) | Observability | Accepted |
| [0018](0018-frontend.md) | Frontend: Next.js PWA with a thin BFF | Accepted |

New ADR: copy [template.md](template.md), take the next number, open it in the same PR as the change it justifies.

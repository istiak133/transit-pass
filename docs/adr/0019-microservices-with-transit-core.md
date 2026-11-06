# 0019. Microservices behind an API gateway, with one Transit Core service

- **Status:** Accepted
- **Date:** 2026-11-06
- **Supersedes:** [0001](0001-modular-monolith.md)

## Context
ADR 0001 chose a modular monolith. The goals of the project have since changed: it must also demonstrate the
mechanics of a service-based system (an API gateway, independent deployables, database per service,
service-to-service authentication), because those are part of the target role.

The hard constraint from ADR 0001 still holds: some steps must be atomic. Buying a ticket debits the wallet and
creates the ticket. An exit tap closes the journey, prices it, and debits the wallet. If these sit in different
services with different databases, one local ACID transaction is no longer possible and every such flow becomes a
saga (reserve, confirm, compensate on failure). That adds failure modes exactly where money moves.

## Decision
Split along the lines where **no atomic write crosses the boundary**:

| Process | Owns | Database | Public routes (via gateway) |
|---|---|---|---|
| **Gateway** (YARP) | Routing, request id, per-client rate limit, later JWT pre-check | none | everything below |
| **Identity** | Accounts, OTP, passwords, sessions, JWT signing keys | `identity_db` | `/api/v1/identity/*` |
| **Network** | Stations, lines, fare schedules, fare quotes | `network_db` | `/api/v1/network/*` |
| **Transit Core** | Wallet + ledger, payments/top-ups, tickets, gate taps, journeys | `core_db` | `/api/v1/wallet/*`, `/api/v1/tickets/*`, `/api/v1/gate/*` |

Rules:
- **Database per service, enforced by PostgreSQL.** One server locally, but each service has its own database and login
  role, and `CONNECT` is revoked from everyone else. A service cannot read another service's tables even by mistake.
- **Clients only talk to the gateway.** Services are not published outside the container network.
- **Inside Transit Core, the modules keep the ADR 0001 rules** (Wallet, Ticketing, Gate and Payments as separate
  modules with their own schemas, enforced by architecture tests), so Core can be split further later.
- **Authentication:** Identity signs ES256 JWTs (ADR 0011). Every service validates them itself with the public keys
  from Identity's JWKS endpoint. The gateway may reject obviously bad tokens early, but services never trust the
  gateway alone.
- **Service-to-service calls:** synchronous HTTP only where the caller needs an answer now, always with a timeout and
  never inside a database transaction. The one case today: Core asks Network for a fare quote **before** it opens the
  purchase transaction, and snapshots the quote into the ticket (ADR 0013).
- **Asynchronous events:** published through the transactional outbox (ADR 0012). A message broker (RabbitMQ) is added
  when a second service needs to consume events. Nothing in the MVP needs one.
- **Identity does not push users to Core.** Core keys everything by the JWT `sub` and creates a wallet on first use,
  so no cross-service write happens at registration.

## Alternatives considered
- **Keep the modular monolith (ADR 0001).** The simplest and still technically right for the load, but it would not
  exercise the gateway, independent deployment, or cross-service auth that this project now needs to show.
- **One service per capability (Wallet, Ticketing, Gate, Payments separately).** Purchase and exit would become sagas
  across three services, with compensations and "money moved but ticket missing" windows. That cost buys nothing
  at this scale and makes the most important invariant (I1, money is always right) the hardest to keep.
- **Shared database for all services.** It keeps transactions but hides coupling: any service can join any table, and
  a schema change in one breaks the others. That is a distributed monolith.
- **Ocelot or a cloud gateway.** YARP is Microsoft's reverse proxy, configured in plain .NET, fast, and testable in
  process. Ocelot is less active. A cloud gateway would tie local development to a vendor.

## Consequences
- Four deployables, so Docker Compose is now the normal way to run the system locally.
- A fare quote needs Network to be up at purchase time. If it is down, purchase fails fast with a clear error
  instead of guessing a fare. A local cache of the fare table in Core is the next step if that matters.
- Each service needs its own health checks, logs and traces. The shared `BuildingBlocks` library keeps those
  identical, and the gateway's `X-Request-Id` ties one call together across processes.
- Splitting Core later is possible, but each split that crosses an atomic write needs a saga and its own ADR.

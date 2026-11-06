# 0001. Modular monolith over microservices

- **Status:** Superseded by [0019](0019-microservices-with-transit-core.md)
- **Date:** 2026-11-04

## Context
TransitPass has distinct business capabilities: identity, wallet (money), fare network, ticketing,
gate/journeys, and payments. Several flows cross them in a single user action. For example, an exit tap
closes a journey, prices it, and debits the wallet. These steps must be atomic, or a passenger is charged
without a completed journey (or the reverse).

The team is one developer. Operational budget is a laptop and a single cloud VM.

## Decision
Build a **modular monolith**: one deployable, one PostgreSQL database, with each capability in its own
module (`Modules/<Name>/{Domain,Application,Infrastructure,Contracts}`).

Rules, enforced by architecture tests in CI:
- A module may reference another module only through its `Contracts` project (public interfaces, DTOs, integration events).
- Each module owns its tables in its own PostgreSQL schema; no module queries another module's tables.
- Cross-module side effects that do not need atomicity travel as integration events via a transactional outbox.

## Alternatives considered
- **Microservices.** They give independent deployment and scaling, but the exit-tap flow above would need
  a saga with compensations across journey, fare, and wallet services, plus a broker, service discovery,
  and distributed tracing, all to serve one team. This trades the hardest correctness problem in the
  system (money) for coordination overhead with no benefit at this scale.
- **Layered monolith (single Domain/Application/Infrastructure projects).** Simple, but nothing stops
  the wallet from reaching into ticketing tables. Boundaries erode, and a later split becomes a rewrite.
  The original project followed this pattern.

## Consequences
- Cross-module invariants use plain ACID transactions; no distributed transactions.
- Extracting a module into a service later is mechanical: its contracts become an HTTP/gRPC API, its outbox
  events go to a broker, its schema moves to its own database. The seams exist from day one.
- One deployable means one scaling unit. Acceptable: the gate path is designed to be cheap per request,
  and stateless instances scale horizontally behind a load balancer.

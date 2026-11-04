# 0010. Journey processing and exit resolution

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
In the original system an exit with insufficient balance or an overstay returned "Invalid", left the ticket `InUse`,
and gave the passenger no way forward (PRD P6). Concurrent scans of one ticket could also open two journeys.

## Decision

**Serialization per credential.** A tap transaction locks the ticket row with `SELECT … FOR UPDATE`, reads the open
journey, applies the transition, posts ledger entries, and commits. The transaction does no network I/O, so the lock
is held for milliseconds. Different passengers never contend with each other. Lock order is always ticket → journey →
account, so concurrent taps cannot deadlock.

**Entry rules.**
- SJ: ticket `Active`, the gate station equals the origin, and there is no open journey.
- SV: no open journey, balance ≥ the current minimum fare, and no arrears.

**Exit resolution** is an `IExitPolicy` that returns either `Open(charges[])` or `Deny(reason, amountNeeded?)`:

| Situation | Result |
|---|---|
| SV exit | Charge = fare(entry → exit) under the fare schedule valid **at entry time** |
| SJ exit at destination | Open, no charge |
| SJ exit beyond destination | Charge = fare(origin → exit) − paid fare |
| SJ exit before destination | Open, no refund |
| Duration > `MaxTripDuration` | + `OverstayPenalty` |
| Charge fits within `balance + overdraft_limit` | Open; the debit uses floor `-overdraft_limit` |
| Charge does not fit | `Deny(INSUFFICIENT_FUNDS, amountNeeded)`; journey stays open; passenger tops up and retaps |
| No open journey (exit without entry) | `Deny(NO_ENTRY_RECORDED)` online; operator can resolve at the station |

Deny reasons are a closed enum shared with the PWA, which maps each reason to a passenger action (PRD invariant I7).

**Same-gate retaps.** A passenger who taps twice at the same gate within 10 seconds gets the original decision
replayed, keyed by `(tid, station, direction)`, so a nervous double scan never produces a second effect.

**System closure.** A job closes journeys open longer than `MaxTripDuration + 2h` at maximum fare plus penalty (GTE-8).

## Alternatives considered
- **Optimistic concurrency on the ticket.** Correct, but a double scan from two gates produces a retry and a confusing
  second response. The pessimistic row lock gives a deterministic winner and a clean "already in transit" for the loser.
- **Deny on any shortfall (original).** Strands passengers, and creates the worst support burden in the system.

## Consequences
- The overdraft limit is real credit exposure. Its maximum is bounded by configuration (default 100 BDT), and arrears block the next entry.
- Fares that depend on entry-time schedules require journeys to record the schedule version at entry.

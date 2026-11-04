# 0009. Online-first, offline-fallback gates

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
A station whose gates depend on a WAN link closes when the link drops (PRD P5). Purely offline gates, on the other hand,
cannot stop one Single Journey ticket from being used at two stations, or know the entry station of a Stored Value exit.

## Decision
Each station runs a **GateAgent** (a .NET worker with local SQLite) between the scanner and the API.

**Online mode (default).** The agent verifies the frame locally (ADR 0007), then sends the tap to the API with an 800 ms
timeout. The API is authoritative: it enforces the journey state machine under row locks and returns open or deny.

**Offline mode.** If the API call fails or times out, a circuit breaker opens and the agent decides locally:
- It verifies the frame cryptographically and checks the local revocation list and replay cache.
- It applies local rules: a Single Journey ticket is accepted at its origin for entry, or at any station for exit.
  Stored Value is accepted if the credential is valid and the passenger is not on the denylist.
- It **writes the tap to the SQLite outbox and fsyncs before the gate opens** (NFR-5).
- When the breaker probe succeeds it syncs taps in device-time order, in batches. Tap IDs make this idempotent (ADR 0006).

**Server-side reconciliation of offline taps:**
| Case | Handling |
|---|---|
| Exit tap arrives before its entry tap (two offline gates syncing out of order) | Journey stays pending pricing; priced when the entry arrives |
| Entry never arrives within 24 h | Priced at maximum fare from the exit station, flagged |
| Single Journey ticket with two entries | Second one marked `Conflicted`, passenger flagged for operator review |
| Charge exceeds overdraft (offline exit could not check balance) | Debited past the floor as `Arrears`; entry blocked until settled |

**Bounded offline risk.** Revocation lists sync every 60 s while online. If a gate has been offline longer than
`MaxOfflineWindow` (default 6 h), it still accepts Single Journey tickets (prepaid, so no credit exposure) and denies new
Stored Value **entries** with `OFFLINE_SV_UNAVAILABLE`. Exits are never denied because of offline state.

## Alternatives considered
- **Always online.** Simplest, but violates G3.
- **Always offline with async sync (pure transit-industry model).** Highest availability, but loses online double-use
  prevention and real-time balance checks. Online-first keeps those whenever the network exists.

## Consequences
- The backend must accept late, out-of-order taps. Tap processing uses `device_time` for business rules and records `received_at` for audit.
- Clock drift between gates affects ordering. Gates sync NTP, and drift beyond 5 s raises a health alert.
- Offline double use of a Single Journey ticket is possible within the 30-second frame window, and is detected afterwards. This trade-off is accepted.

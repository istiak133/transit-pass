# 0013. Network model and fare engine

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The original fare was `max(distance × rate, min)`. Distance came from cumulative positions on one line ordered by an
integer, so interchanges were impossible, and a missing distance silently produced the minimum fare (PRD NET-4).
Fare rule changes took effect mid-journey with no record.

## Decision
- **Graph.** `lines`, `stations`, and `line_stations` (ordered membership). Edges connect adjacent stations with
  `distance_m` (integer metres). An interchange is a station that belongs to more than one line.
- **Shortest path.** Dijkstra over the in-memory graph. The graph is loaded as an **immutable snapshot**, tagged with a
  network version, and swapped atomically (`Volatile.Write`) when the network changes. Readers never lock. For about 100
  stations, all-pairs distances are precomputed into the snapshot, so a quote is an array lookup.
- **Fare schedule.** Versioned rows with `effective_from`: distance slabs (`up_to_m → fare_minor`), `min_fare`,
  `max_fare`, `overstay_penalty`, `max_trip_duration`. Only future-dated versions can be created. History is immutable.
- **Policy.** `IFarePolicy.Price(route, schedule, context)` returns a `FareQuote` with a breakdown. The MVP has a slab
  policy. Capping and concessions are further policies in a chain.
- **Time binding.** A Single Journey ticket stores the quote (amount and schedule version) at purchase. Stored Value prices
  with the schedule effective at **entry time**.
- **Errors.** No path, or a closed station, is a typed `FareError`. No code path returns a default fare.

## Alternatives considered
- **Precomputed fare matrix edited by admins.** Simple lookups, but O(n²) manual data that drifts out of sync with the network.
- **Graph database.** Overkill for about 100 nodes. The graph fits in a few kilobytes of memory.

## Consequences
- A network change invalidates the snapshot. Rebuilding takes milliseconds, and quotes always reference the version they used.
- Admin edits are audited (ADM-1) and cannot rewrite past schedules.

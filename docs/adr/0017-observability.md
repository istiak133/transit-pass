# 0017. Observability

- **Status:** Accepted
- **Date:** 2026-11-04

## Decision
- **OpenTelemetry** for traces, metrics, and logs, exported over OTLP. In development the target is the .NET Aspire dashboard
  container. In production it is any OTLP backend. This is vendor-neutral and has no logging framework lock-in.
- **Logging.** `ILogger` with structured templates, and `LoggerMessage` source generators on hot paths. JSON console in
  containers. Phone numbers and tokens are never logged. Phones are masked by a redaction helper.
- **Correlation.** W3C `traceparent` propagates through the API and the GateAgent. `traceId` appears in every problem-details response.
- **Domain metrics** (`System.Diagnostics.Metrics`):
  `transitpass.taps{direction,result,mode}` · `transitpass.tap.duration` (histogram) ·
  `transitpass.exit.denials{reason}` · `transitpass.ledger.postings{kind}` · `transitpass.outbox.lag` ·
  `transitpass.gate.sync_backlog{device}`.
- **Health.** `/health/live` (process) and `/health/ready` (PostgreSQL required, Redis degraded-only, outbox lag threshold).

## Alternatives considered
- **Serilog.** Excellent sinks, but OpenTelemetry already covers structured logs with one export pipeline. Fewer moving parts.

## Consequences
- k6 results in the README come with matching server-side histograms, not just client timings.

# 0014. API style: Minimal APIs, vertical slices, problem details

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The API serves the PWA, the GateAgent, and admin tooling. It needs consistent contracts, errors, and versioning, without
framework ceremony hiding the important logic.

## Decision
- **Minimal APIs** grouped per module (`MapGroup("/api/v1/wallet")`). Each use case is a **vertical slice**: an endpoint
  mapping, a request/response record, a handler class, and its tests, in one folder.
- **No mediator library.** Handlers are plain classes resolved from DI. MediatR moved to a commercial licence, and an
  in-process mediator adds indirection without a benefit here. Cross-cutting behaviour (validation, idempotency,
  transactions) runs as endpoint filters.
- **Validation.** The built-in .NET 10 Minimal API validation (`AddValidation`, DataAnnotations on records) for shape.
  Domain rules return `Result` errors from the handler.
- **Errors.** RFC 9457 `application/problem+json` everywhere, with a stable machine-readable `code`
  (e.g. `wallet.insufficient_funds`), `traceId`, and field errors for validation. Exceptions never leak messages. Unhandled ones become a generic 500.
- **Result type.** Expected failures are values (`Result<T>` with typed `Error`), not exceptions, and map to status codes at the endpoint edge.
- **Versioning.** URL segment `/api/v1`. Breaking changes get `/v2`, and additive changes do not.
- **Docs.** Built-in OpenAPI generation (`Microsoft.AspNetCore.OpenApi`) with Scalar as the UI, in development only.
- **Pagination.** Cursor-based (`?after=<id>&limit=`) on UUIDv7 keys. This stays stable while data is inserted, unlike offset paging.

## Alternatives considered
- **MVC controllers.** Familiar, but they push code toward "one controller per noun" and filter-attribute sprawl. Minimal API groups give the same organization with less indirection.
- **FluentValidation.** Excellent, but .NET 10's built-in validation covers request shape. Complex rules belong in the domain anyway.

## Consequences
- Every endpoint documents its success and problem responses in OpenAPI.
- Error codes are part of the public contract and are listed in the README.

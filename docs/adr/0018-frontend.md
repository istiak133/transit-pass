# 0018. Frontend: Next.js PWA with a thin BFF

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The passenger app must generate non-exportable device keys, render rotating QR frames while offline, and keep refresh
tokens out of reach of scripts. The scanner UI must use the camera and talk to the local GateAgent.

## Decision
- **Next.js (App Router) + TypeScript + React**, installable as a **PWA** (service worker caches the shell and the current
  credentials, so frames render with no network).
- **BFF pattern.** Next.js route handlers proxy auth calls and own the refresh-token cookie
  (`HttpOnly; Secure; SameSite=Strict`). The browser holds only a short-lived access token in memory.
- **Crypto.** WebCrypto `ECDSA P-256`, `extractable: false`, persisted in IndexedDB. `navigator.storage.persist()` is requested to resist eviction.
- **API types.** A TypeScript client generated from the OpenAPI document, so contracts cannot silently drift.
- **Scanner.** A separate route using `BarcodeDetector` where available, with a WASM QR decoder fallback. It talks only to the GateAgent on the station network.

## Alternatives considered
- **Razor/MVC views (original).** They cannot hold non-exportable keys across sessions offline, and they tie the UI to the API deployment.
- **React Native.** Better device integration (NFC, secure enclave) and the eventual target, but out of scope for a web MVP.
  Credential and API contracts are unchanged by that move.

## Consequences
- Two deployables (API, web) plus the GateAgent. Compose wires them for local development.

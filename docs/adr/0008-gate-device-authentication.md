# 0008. Gate device authentication

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
In the original system any HTTP client could call the scan endpoint and name any station (PRD P3). A gate's identity
has to be cryptographic, and the station has to follow from that identity.

## Decision
- **Enrollment.** An operator creates a device for a station and receives a one-time enrollment code (TTL 15 minutes).
  The GateAgent generates a P-256 key pair locally and submits its public key with the code. The server stores only the
  **public key** and binds it to `(device_id, station_id)`.
- **Request signing.** Every GateAgent request carries:
  ```
  X-Device-Id, X-Timestamp, X-Nonce,
  X-Signature = ECDSA_sign(device_key, METHOD \n PATH \n X-Timestamp \n X-Nonce \n SHA-256(body))
  ```
  The server checks: device active, signature valid, |timestamp − now| ≤ 60 s, and nonce unused (5-minute replay store).
- **Authorization.** The station for every tap is read from the device record. Any station field in the body is ignored.
- **Revocation.** Disabling a device takes effect on its next request.

## Alternatives considered
- **Shared API key / HMAC secret.** To verify an HMAC the server must store the secret itself, not a hash, so a database
  leak leaks every gate's credentials. With asymmetric keys, the server holds nothing worth stealing.
- **mTLS.** Strongest, and the production target, but it needs a private CA, certificate issuance, and TLS-terminating
  infrastructure that passes client certificates through. The request-signature scheme provides the same identity
  guarantee at the application layer and leaves room to add mTLS later.

## Consequences
- The GateAgent's private key is the device's identity. It is stored with file-system permissions restricted to the agent user.
- Signature verification costs about 0.1 ms per request on P-256, which is negligible against NFR-1.

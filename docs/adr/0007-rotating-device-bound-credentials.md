# 0007. Rotating, device-bound QR credentials

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The original QR was a static string valid for 24 hours, looked up in the database (PRD P4, P5). A screenshot was as
good as the phone, and gates could not validate anything without the server.

Two properties are needed: **offline verifiability** (the gate can check the QR alone) and **non-transferability**
(a copy stops working quickly).

## Decision

**Credential.** It is issued by the server when a ticket is bought or a Stored Value pass is activated. It is a compact JWS
(`ES256`, ECDSA P-256), with `kid` in the header and this payload:

```json
{ "v":1, "tid":"<ticket uuid>", "sub":"<passenger uuid>", "typ":"SJ|SV",
  "o":"<origin station>", "d":"<destination station>",
  "nbf":1762214400, "exp":1762300800,
  "cnf":{ "jwk":{ "kty":"EC","crv":"P-256","x":"…","y":"…" } } }
```

`cnf` (RFC 7800, proof-of-possession) binds the credential to the device public key registered for that passenger.

**Device key.** The PWA creates the key with WebCrypto, `extractable: false`, stores it in IndexedDB, and registers the
public half once (OTP-confirmed). Script running in the page can **use** the key but cannot **export** it.

**Frame** (what the QR shows, regenerated every 15 seconds):
```
TP1.<credential>.<unix_ts>.<base64url( ECDSA_sign(device_key, SHA-256(credential || "." || unix_ts)) )>
```
This comes to about 450 characters, which fits a QR at version ~15 with error correction M and scans reliably from a phone screen.

**Gate verification** (identical online and offline):
1. Server signature valid under a trusted `kid`, and `nbf ≤ now ≤ exp`.
2. Device signature valid under `cnf.jwk`.
3. `now − 30s ≤ unix_ts ≤ now + 5s`. The asymmetric window allows for clock skew.
4. `(tid, unix_ts)` not seen before (replay cache), and `tid` not on the revocation list.
5. Business rules (station, journey state) on top.

**Keys.** Signing keys rotate every 90 days. Gates fetch a JWKS document with the current and previous keys, so credentials
issued just before a rotation keep validating. Private keys come from configuration/secret storage, and a KMS-backed
provider can be dropped in behind `ICredentialSigner`.

## Alternatives considered
- **Static random token (original).** Needs the server, and can be shared.
- **HMAC-signed tokens / TOTP.** Every gate would hold the shared secret, so one stolen gate could mint credentials for
  the whole network. With ES256, gates hold only public keys.
- **Ed25519.** Smaller signatures, but WebCrypto Ed25519 support across mobile browsers is newer and uneven, while P-256
  works everywhere and in .NET's built-in `ECDsa`. Revisit later.
- **NFC / HCE.** Better UX, but it needs native apps and gate hardware. It is out of scope for a web MVP and compatible with the same credential later.

## Consequences
- A screenshot is useless after 30 seconds. A live relay (a friend streams the QR in real time) remains possible. That is
  inherent to optical credentials and is mitigated by the one-active-journey rule and anomaly flags.
- Losing the device key (browser storage cleared) means re-binding, which revokes outstanding credentials and re-issues them.
- Gates must keep NTP time. Drift is a health metric.

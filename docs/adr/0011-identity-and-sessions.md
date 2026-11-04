# 0011. Identity, passwords and sessions

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
Passengers log in with a phone number. The original system verified email instead, had no rate limits, kept 30-day
cookies that survived a password change, and generated a JWT that nothing validated.

## Decision
- **Account.** Phone (E.164, unique), display name, password. It is activated by a phone OTP.
- **Password hashing.** `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core` (PBKDF2-HMAC-SHA512, 100k iterations,
  versioned format with rehash-on-login). Full ASP.NET Core Identity is not used, because its cookie/UI-centric model and
  schema do not fit a token API.
- **OTP.** 6 digits from `RandomNumberGenerator`, stored as an HMAC (not plaintext), 5-minute TTL, 5 attempts, 60-second
  resend cooldown, invalidated on use. Delivery goes through `ISmsSender`. Development uses a logging sink, and production
  adapters plug in later.
- **Access token.** JWT, `ES256`, 15 minutes, claims `sub`, `role`, `sid` (session id). Asymmetric signing lets future
  services validate tokens without holding a secret.
- **Refresh token.** 256-bit random opaque value, stored as SHA-256, belonging to a session **family**. Each refresh
  rotates the token. Presenting an already-rotated token is treated as theft and revokes the family (OAuth 2.0 Security BCP).
- **Revocation.** Password change or reset revokes all families for the account. Access tokens expire within 15 minutes.
  Sensitive endpoints (device binding, password change) also check `sid` against the session store, so revocation takes effect immediately.
- **Abuse controls.** Fixed-window limits per IP and sliding-window limits per phone number on register, login, OTP, and reset.
  Responses are uniform: "if the number is registered, an OTP has been sent".
- **Browser storage.** The PWA keeps the access token in memory. The refresh token is an `HttpOnly; Secure; SameSite=Strict`
  cookie scoped to the refresh path, set by the Next.js server, so page scripts can never read it.

## Alternatives considered
- **Cookie-only auth (original).** Fine for server-rendered MVC, but not for a separate SPA/PWA plus device clients.
- **Long-lived JWTs.** Cannot be revoked.
- **External IdP (Keycloak, Entra).** The right choice at scale, but it adds an operational component and hides the
  session logic this project needs to demonstrate. Token claims stay compatible, so migrating later does not change APIs.

## Consequences
- Session lookups add one indexed read on sensitive endpoints only.
- Clock skew tolerance for JWT validation is set to 30 seconds (the default of 5 minutes is too lax for 15-minute tokens).

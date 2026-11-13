using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.Core.Api.Credentials;

/// <summary>What a valid credential says. The gate (Phase 12) acts on these values.</summary>
public sealed record TicketCredentialClaims(
    Guid TicketId, Guid UserId, string From, string To, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string KeyId);

/// <summary>Claim names inside a credential's payload (besides the registered iss, aud, sub, iat, exp).</summary>
public static class TicketClaimNames
{
    public const string TicketId = "tid";
    public const string From = "from";
    public const string To = "to";
}

/// <summary>
/// Checks a ticket credential with PUBLIC keys only, the way a gate does: no database, no network, no secret.
/// Passing proves the credential was signed by Core and not changed since. It does NOT prove the ticket is
/// still unused: a copied QR verifies just as well as the original. The gate (Phase 12) therefore also does a
/// guarded UPDATE on the ticket row (status Active → InJourney), and the second scan of a copy finds 0 rows.
/// Signature = "is it genuine?"; database status = "has it been used?".
/// </summary>
public sealed class TicketCredentialVerifier
{
    /// <summary>
    /// A real credential is about 500 characters. Anything far longer is refused before it is parsed, so a
    /// huge QR payload can't make a gate spend time on base64 and JSON.
    /// </summary>
    public const int MaxLength = 2048;

    private static readonly Error Invalid = Error.Unauthorized("credential.invalid", "This QR code is not a valid TransitPass ticket.");
    private static readonly Error Expired = Error.Unauthorized("credential.expired", "This QR code's ticket has expired.");

    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _parameters;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _skew;

    /// <summary>
    /// <paramref name="publicKeys"/> are the trusted keys, each with its <c>kid</c>: in Core, the keys from
    /// <see cref="QrKeys"/>; on a gate, the keys from the downloaded JWKS document.
    /// </summary>
    public TicketCredentialVerifier(IEnumerable<SecurityKey> publicKeys, QrOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _parameters = new TokenValidationParameters
        {
            IssuerSigningKeys = publicKeys.ToList(),
            // The key is picked by kid only. An unknown kid fails; we never "try every key we have".
            TryAllIssuerSigningKeys = false,
            // Only ES256. "alg: none", HS256 (with the public key as an HMAC secret) and friends are refused
            // before any key is tried: the algorithm comes from us, never from the attacker's header.
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            RequireSignedTokens = true,
            ValidTypes = [QrOptions.TokenType], // an access token (typ "JWT") is not a ticket
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            // Lifetime is checked below against our TimeProvider (testable, and the gate's own clock),
            // after the signature: an expired-but-genuine QR gets its own clear error.
            ValidateLifetime = false,
        };
        _clock = clock;
        _skew = options.ClockSkew;
    }

    /// <summary>The verifier Core registers: its own public keys (current + retired) and the "Qr" settings.</summary>
    public static TicketCredentialVerifier Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        QrKeys keys = services.GetRequiredService<QrKeys>();
        return new TicketCredentialVerifier(
            keys.Verification.Select(k => (SecurityKey)k.SecurityKey),
            services.GetRequiredService<IOptions<QrOptions>>().Value,
            services.GetRequiredService<TimeProvider>());
    }

    /// <summary>The claims of a genuine, unexpired credential, or <c>credential.invalid</c> / <c>credential.expired</c>.</summary>
    public Task<Result<TicketCredentialClaims>> VerifyAsync(string? credential) => VerifyAsync(credential, TimeSpan.Zero);

    /// <summary>
    /// Like <see cref="VerifyAsync(string?)"/>, but a genuine credential that expired less than
    /// <paramref name="acceptExpiredFor"/> ago still passes. The gate uses this for EXITS only: Rafi who entered
    /// at 23:50 must be able to leave at 00:10, after his ticket's service day (and so its QR) has ended.
    /// The signature is checked exactly as strictly; only the expiry window is wider.
    /// </summary>
    public async Task<Result<TicketCredentialClaims>> VerifyAsync(string? credential, TimeSpan acceptExpiredFor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acceptExpiredFor, TimeSpan.Zero);
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > MaxLength)
        {
            return Invalid;
        }

        TokenValidationResult result = await _handler.ValidateTokenAsync(credential, _parameters);
        if (!result.IsValid)
        {
            return Invalid;
        }

        // Genuine. Now: is it inside its lifetime? exp is required; nbf is honoured if present.
        var token = (JsonWebToken)result.SecurityToken;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (!token.TryGetPayloadValue(JwtRegisteredClaimNames.Exp, out long _) || token.ValidFrom > now + _skew)
        {
            return Invalid;
        }

        if (token.ValidTo <= now - _skew - acceptExpiredFor)
        {
            return Expired;
        }

        // Signed by us, but still check the shape: the gate relies on these being a real id and real stations.
        if (!token.TryGetPayloadValue(TicketClaimNames.TicketId, out string? tid) || !Guid.TryParse(tid, out Guid ticketId)
            || !token.TryGetPayloadValue(JwtRegisteredClaimNames.Sub, out string? sub) || !Guid.TryParse(sub, out Guid userId)
            || !token.TryGetPayloadValue(TicketClaimNames.From, out string? from) || string.IsNullOrEmpty(from)
            || !token.TryGetPayloadValue(TicketClaimNames.To, out string? to) || string.IsNullOrEmpty(to))
        {
            return Invalid;
        }

        return new TicketCredentialClaims(
            ticketId, userId, from, to,
            new DateTimeOffset(token.IssuedAt, TimeSpan.Zero),
            new DateTimeOffset(token.ValidTo, TimeSpan.Zero),
            token.Kid);
    }
}

using Microsoft.Extensions.Options;
using TransitPass.BuildingBlocks.Auth;

namespace TransitPass.Core.Api.Credentials;

/// <summary>Settings under "Qr": the key and names for the signed ticket credential inside each QR code.</summary>
public sealed class QrOptions
{
    public const string Section = "Qr";

    /// <summary>
    /// The JWS <c>typ</c> header of a ticket credential. An access token says "JWT", so neither kind of token
    /// can ever be passed off as the other (RFC 8725 §3.11: explicit typing).
    /// </summary>
    public const string TokenType = "transitpass-ticket+jwt";

    /// <summary>
    /// Core's own ES256 private key (EC P-256, PEM). Not Identity's: a leaked login key must not mint tickets,
    /// and a leaked QR key must not mint logins. Never in the repo: comes from .env / a secret store.
    /// </summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>
    /// Public keys of earlier signing keys, kept for verification after a rotation so QR codes issued just
    /// before it keep working until they expire (at most one service day). Each entry is a PEM.
    /// </summary>
    public List<string> RetiredPublicKeyPems { get; set; } = [];

    /// <summary>Who issued the credential (<c>iss</c>). Not Identity's issuer.</summary>
    public string Issuer { get; set; } = "transitpass-core";

    /// <summary>Who the credential is for (<c>aud</c>): the gates. Access tokens are for "transitpass".</summary>
    public string Audience { get; set; } = "transitpass-gate";

    /// <summary>Allowed clock difference between Core and a gate when checking <c>exp</c>.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The QR keys, loaded once at startup: one key that signs, and the public keys that verify
/// (current first, then retired ones). The verify list holds public-only copies, so code that only
/// verifies has nothing it could sign with.
/// </summary>
public sealed class QrKeys : IDisposable
{
    internal const string Setting = "Qr:SigningKeyPem";

    public QrKeys(IOptions<QrOptions> options, IHostEnvironment env, ILogger<QrKeys> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        Signing = Es256Key.LoadSigningKey(options.Value.SigningKeyPem, Setting, env, logger);

        List<Es256Key> verify = [Signing.PublicOnly()];
        HashSet<string> kids = [Signing.KeyId];
        for (int i = 0; i < options.Value.RetiredPublicKeyPems.Count; i++)
        {
            using Es256Key loaded = Es256Key.FromPem(options.Value.RetiredPublicKeyPems[i], $"Qr:RetiredPublicKeyPems:{i}");
            if (kids.Add(loaded.KeyId)) // the same key listed twice (or the current one again) is published once
            {
                verify.Add(loaded.PublicOnly()); // even if someone pasted a private PEM, keep only the public half
            }
        }

        Verification = verify;
        Jwks = new { keys = verify.Select(k => k.ToPublicJwk()).ToArray() };
    }

    /// <summary>The current private key. Only <see cref="TicketCredentialIssuer"/> uses it.</summary>
    public Es256Key Signing { get; }

    /// <summary>Public-only keys: the current one, then retired ones.</summary>
    public IReadOnlyList<Es256Key> Verification { get; }

    /// <summary>The JWKS document gates download: <c>{"keys":[…]}</c>.</summary>
    public object Jwks { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Signing.Dispose();
        foreach (Es256Key key in Verification)
        {
            key.Dispose();
        }
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.BuildingBlocks.Auth;

/// <summary>One public key as a JWK (RFC 7517): x and y only, never the private <c>d</c>.</summary>
public sealed record PublicJwk(string Kty, string Crv, string Use, string Alg, string Kid, string X, string Y);

/// <summary>
/// An ES256 (ECDSA P-256 + SHA-256) key loaded from PEM. Shared by every service that signs something:
/// Identity signs access tokens with one, Core signs ticket QR credentials with another. Each service owns
/// its own key; only the public halves ever leave it (through a JWKS endpoint).
/// </summary>
public sealed class Es256Key : IDisposable
{
    private readonly ECDsa _ecdsa;

    private Es256Key(ECDsa ecdsa, bool hasPrivateKey)
    {
        _ecdsa = ecdsa;
        HasPrivateKey = hasPrivateKey;

        var key = new ECDsaSecurityKey(ecdsa);
        // kid = RFC 7638 thumbprint of the public key: the same key always gets the same id,
        // and a new key gets a new id, so verifiers can pick the right key during rotation.
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        SecurityKey = key;
        Credentials = hasPrivateKey ? new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256) : null;
    }

    /// <summary>The key, with its <c>kid</c>.</summary>
    public ECDsaSecurityKey SecurityKey { get; }

    /// <summary>The key id (RFC 7638 thumbprint).</summary>
    public string KeyId => SecurityKey.KeyId;

    /// <summary>True when this key can sign; false for a verify-only (public) key.</summary>
    public bool HasPrivateKey { get; }

    /// <summary>What a token handler signs with. Null for a verify-only key.</summary>
    public SigningCredentials? Credentials { get; }

    /// <summary>
    /// The signing key for a service. A configured PEM always wins. With no PEM, Development and Testing get a
    /// random key and a warning (tokens stop verifying after a restart); every other environment fails fast,
    /// because a production service must never invent its own key silently.
    /// </summary>
    /// <param name="pem">PKCS#8 or SEC1 private key PEM; a one-line PEM with literal <c>\n</c> (as in .env) is accepted.</param>
    /// <param name="setting">The configuration key, used in error and log messages (for example <c>Jwt:SigningKeyPem</c>).</param>
    /// <param name="env">Decides whether a missing key may be generated (Development/Testing only).</param>
    /// <param name="logger">Receives the "temporary key" warning.</param>
    public static Es256Key LoadSigningKey(string? pem, string setting, IHostEnvironment env, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(logger);

        if (!string.IsNullOrWhiteSpace(pem))
        {
            var key = FromPem(pem, setting);
            if (!key.HasPrivateKey)
            {
                key.Dispose();
                throw new InvalidOperationException($"{setting} must be a private key: a public key cannot sign.");
            }

            return key;
        }

        if (!env.IsDevelopment() && !env.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException($"{setting} is required outside Development.");
        }

        logger.LogWarning("No {Setting} configured: using a temporary key. Anything it signs stops verifying after a restart.", setting);
        return Generate();
    }

    /// <summary>A brand-new random P-256 key pair.</summary>
    public static Es256Key Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), hasPrivateKey: true);

    /// <summary>Loads a private or public P-256 key from PEM. Any other curve or size is refused.</summary>
    public static Es256Key FromPem(string pem, string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        var ecdsa = ECDsa.Create();
        try
        {
            // Tolerate a PEM kept on one line with literal "\n" (as in .env files).
            ecdsa.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
            ECParameters p = ecdsa.ExportParameters(includePrivateParameters: false);
            if (ecdsa.KeySize != 256 || p.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new InvalidOperationException($"{setting} must be an EC P-256 key (ES256).");
            }

            return new Es256Key(ecdsa, hasPrivateKey: HasPrivate(ecdsa));
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            // Not PEM at all (ArgumentException), or PEM holding something that isn't an EC key, such as RSA.
            ecdsa.Dispose();
            throw new InvalidOperationException($"{setting} is not a valid PEM key.", e);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A verify-only copy: a separate key object holding just the public point. Whoever gets it can check
    /// signatures but has nothing to sign with.
    /// </summary>
    public Es256Key PublicOnly()
    {
        ECParameters p = _ecdsa.ExportParameters(includePrivateParameters: false);
        return new Es256Key(ECDsa.Create(p), hasPrivateKey: false);
    }

    /// <summary>The public key as a JWK, ready for a <c>{"keys":[…]}</c> document.</summary>
    public PublicJwk ToPublicJwk()
    {
        ECParameters p = _ecdsa.ExportParameters(includePrivateParameters: false);
        return new PublicJwk("EC", "P-256", "sig", "ES256", KeyId, Base64UrlEncoder.Encode(p.Q.X), Base64UrlEncoder.Encode(p.Q.Y));
    }

    private static bool HasPrivate(ECDsa ecdsa)
    {
        try
        {
            return ecdsa.ExportParameters(includePrivateParameters: true).D is { Length: > 0 };
        }
        catch (CryptographicException)
        {
            return false; // a public-only key has no D to export
        }
    }

    /// <inheritdoc />
    public void Dispose() => _ecdsa.Dispose();
}

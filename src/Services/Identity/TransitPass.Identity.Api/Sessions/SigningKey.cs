using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>
/// The ES256 (ECDSA P-256 + SHA-256) key pair. Only Identity holds the private half; every other
/// service gets the public half from the JWKS endpoint and can verify tokens but never create them.
/// </summary>
public sealed class SigningKey : IDisposable
{
    private readonly ECDsa _ecdsa;

    public SigningKey(IOptions<JwtOptions> options, IHostEnvironment env, ILogger<SigningKey> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(env);
        string? pem = options.Value.SigningKeyPem;

        _ecdsa = ECDsa.Create();
        if (!string.IsNullOrWhiteSpace(pem))
        {
            // Tolerate a PEM kept on one line with literal "\n" (as in .env files).
            _ecdsa.ImportFromPem(pem.Replace("\\n", "\n", StringComparison.Ordinal));
            if (_ecdsa.KeySize != 256)
            {
                throw new InvalidOperationException("Jwt:SigningKeyPem must be an EC P-256 key (ES256).");
            }
        }
        else if (env.IsDevelopment() || env.IsEnvironment("Testing"))
        {
            _ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
            logger.LogWarning("No Jwt:SigningKeyPem configured: using a temporary key. Tokens stop verifying after a restart.");
        }
        else
        {
            // Fail fast: a production service must never invent its own key silently.
            throw new InvalidOperationException("Jwt:SigningKeyPem is required outside Development.");
        }

        var key = new ECDsaSecurityKey(_ecdsa);
        // kid = RFC 7638 thumbprint of the public key: the same key always gets the same id,
        // and a new key gets a new id, so verifiers can pick the right key during rotation.
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        SecurityKey = key;
        Credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
    }

    /// <summary>The key, with its <c>kid</c>.</summary>
    public ECDsaSecurityKey SecurityKey { get; }

    /// <summary>What the token handler signs with.</summary>
    public SigningCredentials Credentials { get; }

    /// <summary>The public key as a JWK: x and y only, never the private <c>d</c>.</summary>
    public object PublicJwk()
    {
        ECParameters p = _ecdsa.ExportParameters(includePrivateParameters: false);
        return new
        {
            kty = "EC",
            crv = "P-256",
            use = "sig",
            alg = "ES256",
            kid = SecurityKey.KeyId,
            x = Base64UrlEncoder.Encode(p.Q.X),
            y = Base64UrlEncoder.Encode(p.Q.Y),
        };
    }

    /// <inheritdoc />
    public void Dispose() => _ecdsa.Dispose();
}

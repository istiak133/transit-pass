using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TransitPass.BuildingBlocks.Auth;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>
/// The ES256 (ECDSA P-256 + SHA-256) key pair for access tokens. Only Identity holds the private half; every
/// other service gets the public half from the JWKS endpoint and can verify tokens but never create them.
/// Loading, the dev fallback and the fail-fast rule live in <see cref="Es256Key"/>, shared with Core's QR key.
/// </summary>
public sealed class SigningKey : IDisposable
{
    private readonly Es256Key _key;

    public SigningKey(IOptions<JwtOptions> options, IHostEnvironment env, ILogger<SigningKey> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _key = Es256Key.LoadSigningKey(options.Value.SigningKeyPem, "Jwt:SigningKeyPem", env, logger);
    }

    /// <summary>The key, with its <c>kid</c>.</summary>
    public ECDsaSecurityKey SecurityKey => _key.SecurityKey;

    /// <summary>What the token handler signs with.</summary>
    public SigningCredentials Credentials => _key.Credentials!;

    /// <summary>The public key as a JWK: x and y only, never the private <c>d</c>.</summary>
    public PublicJwk PublicJwk() => _key.ToPublicJwk();

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();
}

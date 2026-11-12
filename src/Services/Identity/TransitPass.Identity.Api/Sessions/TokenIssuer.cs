using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.Identity.Api.Sessions;

/// <summary>Creates signed access tokens (JWS, compact form: header.payload.signature).</summary>
public sealed class TokenIssuer(SigningKey key, IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>How long each issued token is valid.</summary>
    public TimeSpan Lifetime => options.Value.AccessTokenLifetime;

    /// <summary>A signed token for this user, and when it expires.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = now + options.Value.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = options.Value.Audience,
            // sub = who; jti = this token's own id (lets a future deny-list revoke one token).
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7(now).ToString()),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = key.Credentials, // ES256, header gets "kid"
        };

        return (_handler.CreateToken(descriptor), expires);
    }
}

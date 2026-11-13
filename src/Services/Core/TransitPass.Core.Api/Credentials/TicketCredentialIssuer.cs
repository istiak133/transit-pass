using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Api.Credentials;

/// <summary>
/// Signs a ticket's QR credential: a compact JWS (header.payload.signature), ES256, with Core's QR key.
/// <code>
/// header  { "alg":"ES256", "kid":"…", "typ":"transitpass-ticket+jwt" }
/// payload { "iss":"transitpass-core", "aud":"transitpass-gate", "tid":"…", "sub":"…",
///           "from":"uttara-north", "to":"motijheel", "iat":…, "exp":&lt;ticket expires_at&gt; }
/// </code>
/// The payload is only base64url, not encrypted: anyone can read it, nobody can change it.
/// </summary>
public sealed class TicketCredentialIssuer(QrKeys keys, IOptions<QrOptions> options)
{
    // No automatic nbf/iat/exp: we write iat and exp ourselves, and exp must be the ticket's own expiry.
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>A credential for this ticket, valid until the ticket itself expires.</summary>
    public string Issue(Ticket ticket, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        QrOptions o = options.Value;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = QrOptions.TokenType,
            Issuer = o.Issuer,
            Audience = o.Audience,
            Claims = new Dictionary<string, object>
            {
                [TicketClaimNames.TicketId] = ticket.Id.ToString(),
                [JwtRegisteredClaimNames.Sub] = ticket.UserId.ToString(),
                [TicketClaimNames.From] = ticket.FromStation,
                [TicketClaimNames.To] = ticket.ToStation,
            },
            IssuedAt = now.UtcDateTime,
            Expires = ticket.ExpiresAt.UtcDateTime,
            SigningCredentials = keys.Signing.Credentials, // ES256, header gets "kid"
        });
    }
}

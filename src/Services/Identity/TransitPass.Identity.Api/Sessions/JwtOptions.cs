namespace TransitPass.Identity.Api.Sessions;

/// <summary>Settings for the access tokens Identity issues. Bound from the <c>Jwt</c> configuration section.</summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Jwt";

    /// <summary>Who issued the token (<c>iss</c>). Verifiers reject any other issuer.</summary>
    public string Issuer { get; set; } = "transitpass-identity";

    /// <summary>Who the token is for (<c>aud</c>). Every TransitPass service expects this value.</summary>
    public string Audience { get; set; } = "transitpass";

    /// <summary>How long an access token is valid. Short, because a JWT can't be recalled once issued.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The ES256 private key as PKCS#8 PEM. Required outside Development; supply it through user secrets
    /// or an environment variable, never in appsettings.json. Empty in Development means "generate one at startup".
    /// </summary>
    public string? SigningKeyPem { get; set; }
}

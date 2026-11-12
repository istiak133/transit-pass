namespace TransitPass.BuildingBlocks.Auth;

/// <summary>How a service verifies Identity's tokens. Bound from the <c>Jwt</c> section.</summary>
public sealed class JwtVerificationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Jwt";

    /// <summary>Where Identity publishes its public keys, e.g. <c>http://identity:8080/.well-known/jwks.json</c>.</summary>
    public string JwksUrl { get; set; } = "http://localhost:5101/.well-known/jwks.json";

    /// <summary>Required <c>iss</c>. Must match what Identity writes.</summary>
    public string Issuer { get; set; } = "transitpass-identity";

    /// <summary>Required <c>aud</c>.</summary>
    public string Audience { get; set; } = "transitpass";

    /// <summary>
    /// Refuse to fetch keys over plain HTTP. True by default; turned off only for traffic inside a private
    /// network (Compose, local runs), where services talk HTTP to each other.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// The shortest gap between two forced key fetches (when a token has an unknown <c>kid</c>).
    /// Stops a flood of forged tokens from turning into a flood of requests to Identity.
    /// </summary>
    public TimeSpan KeyRefreshMinInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Re-fetch keys at least this often even without unknown kids, to pick up rotations.</summary>
    public TimeSpan KeyRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Allowed clock difference between Identity and this service when checking exp/nbf.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

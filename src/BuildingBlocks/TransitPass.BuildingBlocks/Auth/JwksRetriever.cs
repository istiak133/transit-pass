using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TransitPass.BuildingBlocks.Auth;

/// <summary>
/// Teaches the token library's <see cref="ConfigurationManager{T}"/> to read a bare JWKS document.
/// The library normally reads a full OpenID Connect discovery document; we only need the keys, so we
/// wrap them in an <see cref="OpenIdConnectConfiguration"/>. The manager then gives us caching,
/// periodic refresh, refresh on an unknown kid, and "keep the last good keys if a fetch fails".
/// </summary>
internal sealed class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        string json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var config = new OpenIdConnectConfiguration();
        foreach (SecurityKey key in new JsonWebKeySet(json).GetSigningKeys())
        {
            config.SigningKeys.Add(key);
        }

        return config;
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransitPass.Identity.Api.Sessions;

namespace TransitPass.Identity.Tests;

public sealed class SigningKeyTests
{
    private static SigningKey Create(string? pem, string environment) => new(
        Options.Create(new JwtOptions { SigningKeyPem = pem }),
        new HostingEnvironment { EnvironmentName = environment },
        NullLogger<SigningKey>.Instance);

    [Fact]
    public void Production_without_a_key_refuses_to_start()
    {
        Should.Throw<InvalidOperationException>(() => Create(null, Environments.Production));
    }

    [Fact]
    public void Same_pem_gives_the_same_kid_so_restarts_keep_tokens_valid()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = ec.ExportPkcs8PrivateKeyPem();

        using var a = Create(pem, Environments.Production);
        using var b = Create(pem, Environments.Production);
        a.SecurityKey.KeyId.ShouldBe(b.SecurityKey.KeyId);
    }

    [Fact]
    public void A_non_p256_key_is_rejected()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Should.Throw<InvalidOperationException>(() => Create(ec.ExportPkcs8PrivateKeyPem(), Environments.Production));
    }
}

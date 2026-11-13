using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using TransitPass.BuildingBlocks.Auth;

namespace TransitPass.BuildingBlocks.Tests;

/// <summary>The shared ES256 key loader used by Identity (login tokens) and Core (QR credentials).</summary>
public sealed class Es256KeyTests
{
    private static Es256Key Load(string? pem, string environment) => Es256Key.LoadSigningKey(
        pem, "Test:SigningKeyPem", new HostingEnvironment { EnvironmentName = environment }, NullLogger.Instance);

    [Fact]
    public void A_configured_private_pem_signs_and_keeps_its_kid_across_restarts()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = ec.ExportPkcs8PrivateKeyPem();

        using var a = Load(pem, Environments.Production);
        using var b = Load(pem, Environments.Production);

        a.HasPrivateKey.ShouldBeTrue();
        a.Credentials.ShouldNotBeNull();
        a.KeyId.ShouldBe(b.KeyId);
        a.KeyId.Length.ShouldBe(43); // base64url of a SHA-256 thumbprint
    }

    [Fact]
    public void A_one_line_pem_with_escaped_newlines_is_accepted()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = ec.ExportPkcs8PrivateKeyPem();

        using var multi = Es256Key.FromPem(pem, "x");
        using var oneLine = Es256Key.FromPem(pem.Replace("\n", "\\n", StringComparison.Ordinal), "x");

        oneLine.KeyId.ShouldBe(multi.KeyId);
    }

    [Fact]
    public void Production_without_a_key_refuses_to_start_but_development_and_testing_generate_one()
    {
        Should.Throw<InvalidOperationException>(() => Load(null, Environments.Production))
            .Message.ShouldContain("Test:SigningKeyPem");
        Should.Throw<InvalidOperationException>(() => Load("  ", Environments.Staging));

        using var dev = Load(null, Environments.Development);
        using var test = Load(null, "Testing");
        dev.HasPrivateKey.ShouldBeTrue();
        dev.KeyId.ShouldNotBe(test.KeyId); // random each time
    }

    [Fact]
    public void A_public_key_loads_for_verifying_but_cannot_be_the_signing_key()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicPem = ec.ExportSubjectPublicKeyInfoPem();

        using var verifyOnly = Es256Key.FromPem(publicPem, "x");
        verifyOnly.HasPrivateKey.ShouldBeFalse();
        verifyOnly.Credentials.ShouldBeNull();

        Should.Throw<InvalidOperationException>(() => Load(publicPem, Environments.Production))
            .Message.ShouldContain("private key");
    }

    [Fact]
    public void Other_curves_and_garbage_are_rejected()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        Should.Throw<InvalidOperationException>(() => Es256Key.FromPem(p384.ExportPkcs8PrivateKeyPem(), "x"));
        Should.Throw<InvalidOperationException>(() => Es256Key.FromPem(rsa.ExportPkcs8PrivateKeyPem(), "x"));
        Should.Throw<InvalidOperationException>(() => Es256Key.FromPem("not a pem", "x"));
    }

    [Fact]
    public void Public_only_copy_has_the_same_kid_and_nothing_to_sign_with()
    {
        using var key = Es256Key.Generate();
        using var copy = key.PublicOnly();

        copy.KeyId.ShouldBe(key.KeyId);
        copy.HasPrivateKey.ShouldBeFalse();
        copy.Credentials.ShouldBeNull();
    }

    [Fact]
    public void Public_jwk_carries_x_and_y_only()
    {
        using var key = Es256Key.Generate();
        PublicJwk jwk = key.ToPublicJwk();

        jwk.ShouldBe(jwk with { Kty = "EC", Crv = "P-256", Use = "sig", Alg = "ES256", Kid = key.KeyId });
        jwk.X.Length.ShouldBe(43); // 32 bytes, base64url
        jwk.Y.Length.ShouldBe(43);
        System.Text.Json.JsonSerializer.Serialize(jwk).ShouldNotContain("\"D\"", Case.Insensitive);
    }
}

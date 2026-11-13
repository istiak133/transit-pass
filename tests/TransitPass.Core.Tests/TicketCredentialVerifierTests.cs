using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Credentials;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The QR credential on its own, with no database and no HTTP: sign it, then attack it every way a passenger
/// with a QR editor could. Only a genuine, unexpired, correctly typed credential may pass.
/// </summary>
public sealed class TicketCredentialVerifierTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 4, 0, 0, TimeSpan.Zero); // 10:00 in Dhaka
    private readonly ManualClock _clock = new(Now);
    private readonly QrOptions _options = new();
    private readonly QrKeys _keys;
    private readonly TicketCredentialIssuer _issuer;
    private readonly TicketCredentialVerifier _verifier;
    private readonly List<IDisposable> _owned = [];

    public TicketCredentialVerifierTests()
    {
        _keys = NewKeys();
        _issuer = new TicketCredentialIssuer(_keys, Options.Create(_options));
        _verifier = VerifierFor(_keys);
    }

    public void Dispose()
    {
        _owned.ForEach(d => d.Dispose());
        _keys.Dispose();
    }

    // ---------- helpers ----------

    private static Ticket NewTicket() => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        FromStation = "uttara-north",
        ToStation = "motijheel",
        FarePoisha = 100_00,
        DistanceM = 16_500,
        Status = TicketStatus.Active,
        PurchasedAt = Now,
        ExpiresAt = Ticket.ExpiryFor(Now), // 2026-11-13T18:00Z
    };

    private static QrKeys NewKeys(string? pem = null, params string[] retired) => new(
        Options.Create(new QrOptions { SigningKeyPem = pem, RetiredPublicKeyPems = [.. retired] }),
        new HostingEnvironment { EnvironmentName = "Testing" },
        NullLogger<QrKeys>.Instance);

    private TicketCredentialVerifier VerifierFor(QrKeys keys) =>
        new(keys.Verification.Select(k => (SecurityKey)k.SecurityKey), _options, _clock);

    private static string B64(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

    private static JsonObject Part(string credential, int index) =>
        JsonNode.Parse(Base64UrlEncoder.Decode(credential.Split('.')[index]))!.AsObject();

    /// <summary>Signs any header + payload with any EC key, exactly as an attacker with their own key could.</summary>
    private static string SignRaw(JsonObject header, JsonObject payload, ECDsa key)
    {
        string signingInput = B64(header.ToJsonString()) + "." + B64(payload.ToJsonString());
        byte[] sig = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + Base64UrlEncoder.Encode(sig);
    }

    private ECDsa NewEc()
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _owned.Add(ec);
        return ec;
    }

    private async Task ShouldBeRejected(string credential, string code = "credential.invalid")
    {
        Result<TicketCredentialClaims> result = await _verifier.VerifyAsync(credential);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(code);
        result.Error.Type.ShouldBe(ErrorType.Unauthorized);
    }

    // ---------- the genuine credential ----------

    [Fact]
    public async Task A_genuine_credential_verifies_and_returns_the_ticket_claims()
    {
        Ticket ticket = NewTicket();
        string credential = _issuer.Issue(ticket, Now);

        var result = await _verifier.VerifyAsync(credential);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new TicketCredentialClaims(
            ticket.Id, ticket.UserId, "uttara-north", "motijheel", Now, ticket.ExpiresAt, _keys.Signing.KeyId));
    }

    [Fact]
    public void The_header_and_payload_hold_exactly_what_the_gate_needs()
    {
        Ticket ticket = NewTicket();
        string credential = _issuer.Issue(ticket, Now);

        credential.Split('.').Length.ShouldBe(3); // compact JWS: header.payload.signature
        JsonObject header = Part(credential, 0);
        header["alg"]!.GetValue<string>().ShouldBe("ES256");
        header["typ"]!.GetValue<string>().ShouldBe("transitpass-ticket+jwt");
        header["kid"]!.GetValue<string>().ShouldBe(_keys.Signing.KeyId);

        JsonObject payload = Part(credential, 1);
        payload.Select(p => p.Key).Order().ShouldBe(["aud", "exp", "from", "iat", "iss", "sub", "tid", "to"]);
        payload["iss"]!.GetValue<string>().ShouldBe("transitpass-core");
        payload["aud"]!.GetValue<string>().ShouldBe("transitpass-gate");
        payload["tid"]!.GetValue<string>().ShouldBe(ticket.Id.ToString());
        payload["exp"]!.GetValue<long>().ShouldBe(ticket.ExpiresAt.ToUnixTimeSeconds());
        payload["iat"]!.GetValue<long>().ShouldBe(Now.ToUnixTimeSeconds());
        credential.Length.ShouldBeLessThan(TicketCredentialVerifier.MaxLength);
    }

    // ---------- tampering ----------

    [Fact]
    public async Task Changing_the_payload_breaks_the_signature()
    {
        string credential = _issuer.Issue(NewTicket(), Now);
        string[] parts = credential.Split('.');
        JsonObject payload = Part(credential, 1);
        payload["to"] = "kamalapur"; // ride further than paid for

        await ShouldBeRejected($"{parts[0]}.{B64(payload.ToJsonString())}.{parts[2]}");
    }

    [Fact]
    public async Task Changing_the_signature_is_rejected()
    {
        string credential = _issuer.Issue(NewTicket(), Now);
        string[] parts = credential.Split('.');
        byte[] sig = Base64UrlEncoder.DecodeBytes(parts[2]);
        sig[10] ^= 0x01; // one bit

        await ShouldBeRejected($"{parts[0]}.{parts[1]}.{Base64UrlEncoder.Encode(sig)}");
        await ShouldBeRejected($"{parts[0]}.{parts[1]}.");
        await ShouldBeRejected($"{parts[0]}.{parts[1]}");
    }

    [Fact]
    public async Task A_credential_signed_with_another_key_is_rejected_even_with_our_kid()
    {
        string genuine = _issuer.Issue(NewTicket(), Now);
        ECDsa attackerKey = NewEc();

        // Same header (our kid!) and payload, signed by the attacker's own P-256 key.
        await ShouldBeRejected(SignRaw(Part(genuine, 0), Part(genuine, 1), attackerKey));

        // A properly built token under the attacker's key and kid: an unknown kid is not tried at all.
        var forged = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = QrOptions.TokenType,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Claims = Part(genuine, 1).ToDictionary(p => p.Key, p => (object)p.Value!.ToString()),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(attackerKey) { KeyId = "attacker" }, SecurityAlgorithms.EcdsaSha256),
        });
        await ShouldBeRejected(forged);
    }

    // ---------- algorithm confusion ----------

    [Fact]
    public async Task Alg_none_is_rejected()
    {
        string genuine = _issuer.Issue(NewTicket(), Now);
        JsonObject header = Part(genuine, 0);
        header["alg"] = "none";
        string body = $"{B64(header.ToJsonString())}.{genuine.Split('.')[1]}";

        await ShouldBeRejected(body + ".");
        await ShouldBeRejected(body + "." + genuine.Split('.')[2]); // with the old signature kept, too
    }

    [Fact]
    public async Task Hs256_using_our_public_key_as_the_hmac_secret_is_rejected()
    {
        // The classic attack: the public key is public, so if a verifier let the header choose HS256,
        // anyone could MAC a token with the public key bytes as the "secret".
        string genuine = _issuer.Issue(NewTicket(), Now);
        JsonObject header = Part(genuine, 0);
        header["alg"] = "HS256";
        string signingInput = $"{B64(header.ToJsonString())}.{genuine.Split('.')[1]}";
        byte[] secret = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_keys.Signing.ToPublicJwk()));
        byte[] mac = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(signingInput));

        await ShouldBeRejected(signingInput + "." + Base64UrlEncoder.Encode(mac));
    }

    // ---------- the wrong kind of token ----------

    [Fact]
    public async Task A_login_access_token_is_not_a_ticket_even_if_signed_with_the_qr_key()
    {
        // What Identity issues: typ JWT, iss transitpass-identity, aud transitpass. Even under OUR key it fails.
        string accessToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "transitpass-identity",
            Audience = "transitpass",
            Subject = new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString()), new Claim("tid", Guid.NewGuid().ToString())]),
            Expires = Now.AddMinutes(15).UtcDateTime,
            IssuedAt = Now.UtcDateTime,
            NotBefore = Now.UtcDateTime,
            SigningCredentials = _keys.Signing.Credentials,
        });
        Part(accessToken, 0)["typ"]!.GetValue<string>().ShouldBe("JWT");

        await ShouldBeRejected(accessToken);
    }

    [Theory]
    [InlineData("typ", "JWT")]
    [InlineData("typ", "at+jwt")]
    [InlineData("iss", "transitpass-identity")]
    [InlineData("aud", "transitpass")]
    public async Task One_wrong_typ_issuer_or_audience_is_enough_to_reject(string field, string value)
    {
        string genuine = _issuer.Issue(NewTicket(), Now);
        JsonObject header = Part(genuine, 0), payload = Part(genuine, 1);
        (field == "typ" ? header : payload)[field] = value;

        // Re-signed with the REAL key: the signature is valid, only the one field is wrong.
        using var ec = ECDsa.Create(_keys.Signing.SecurityKey.ECDsa.ExportParameters(true));
        (await _verifier.VerifyAsync(SignRaw(Part(genuine, 0), Part(genuine, 1), ec))).IsSuccess.ShouldBeTrue(); // control
        await ShouldBeRejected(SignRaw(header, payload, ec));
    }

    [Fact]
    public async Task Missing_typ_or_exp_or_a_bad_ticket_id_is_rejected()
    {
        string genuine = _issuer.Issue(NewTicket(), Now);
        using var ec = ECDsa.Create(_keys.Signing.SecurityKey.ECDsa.ExportParameters(true));

        JsonObject noTyp = Part(genuine, 0);
        noTyp.Remove("typ");
        await ShouldBeRejected(SignRaw(noTyp, Part(genuine, 1), ec));

        JsonObject noExp = Part(genuine, 1);
        noExp.Remove("exp");
        await ShouldBeRejected(SignRaw(Part(genuine, 0), noExp, ec));

        JsonObject badTid = Part(genuine, 1);
        badTid["tid"] = "12345";
        await ShouldBeRejected(SignRaw(Part(genuine, 0), badTid, ec));

        JsonObject noTo = Part(genuine, 1);
        noTo.Remove("to");
        await ShouldBeRejected(SignRaw(Part(genuine, 0), noTo, ec));

        JsonObject futureNbf = Part(genuine, 1);
        futureNbf["nbf"] = Now.AddHours(1).ToUnixTimeSeconds();
        await ShouldBeRejected(SignRaw(Part(genuine, 0), futureNbf, ec));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jws")]
    [InlineData("a.b.c")]
    public async Task Garbage_is_rejected(string? credential) => await ShouldBeRejected(credential!);

    [Fact]
    public async Task An_oversized_credential_is_rejected_before_parsing()
    {
        string genuine = _issuer.Issue(NewTicket(), Now);
        await ShouldBeRejected(genuine + new string('A', TicketCredentialVerifier.MaxLength));
    }

    // ---------- time ----------

    [Fact]
    public async Task After_midnight_the_credential_is_expired_with_a_small_clock_skew()
    {
        Ticket ticket = NewTicket();
        string credential = _issuer.Issue(ticket, Now);

        _clock.Now = ticket.ExpiresAt.AddSeconds(20); // gate clock 20 s ahead: still inside the 30 s skew
        (await _verifier.VerifyAsync(credential)).IsSuccess.ShouldBeTrue();

        _clock.Now = ticket.ExpiresAt.AddSeconds(31);
        await ShouldBeRejected(credential, "credential.expired");
    }

    [Fact]
    public async Task An_expired_forgery_is_invalid_not_expired()
    {
        // The signature is checked first: a forgery never learns whether its dates would have passed.
        string credential = _issuer.Issue(NewTicket(), Now);
        string[] p = credential.Split('.');
        _clock.Now = Now.AddDays(2);

        await ShouldBeRejected($"{p[0]}.{p[1]}.{p[2][..^4]}AAAA");
    }

    // ---------- rotation ----------

    [Fact]
    public async Task After_a_rotation_old_qr_codes_verify_through_the_retired_key_by_kid()
    {
        using var oldEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var before = NewKeys(oldEc.ExportPkcs8PrivateKeyPem());
        string oldQr = new TicketCredentialIssuer(before, Options.Create(_options)).Issue(NewTicket(), Now);

        // Rotate: new signing key, the old one kept as a retired PUBLIC key.
        using var after = NewKeys(newEc.ExportPkcs8PrivateKeyPem(), oldEc.ExportSubjectPublicKeyInfoPem());
        string newQr = new TicketCredentialIssuer(after, Options.Create(_options)).Issue(NewTicket(), Now);
        var verifier = VerifierFor(after);

        after.Verification.Select(k => k.KeyId).ShouldBe([after.Signing.KeyId, before.Signing.KeyId]);
        after.Verification.ShouldAllBe(k => !k.HasPrivateKey);
        (await verifier.VerifyAsync(oldQr)).Value.KeyId.ShouldBe(before.Signing.KeyId);
        (await verifier.VerifyAsync(newQr)).Value.KeyId.ShouldBe(after.Signing.KeyId);

        // Once the old key is dropped (its QR codes have all expired), it is no longer trusted.
        using var later = NewKeys(newEc.ExportPkcs8PrivateKeyPem());
        (await VerifierFor(later).VerifyAsync(oldQr)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void A_retired_key_listed_twice_or_equal_to_the_current_key_is_published_once()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var old = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string oldPub = old.ExportSubjectPublicKeyInfoPem();

        using var keys = NewKeys(ec.ExportPkcs8PrivateKeyPem(), oldPub, oldPub, ec.ExportSubjectPublicKeyInfoPem(), old.ExportPkcs8PrivateKeyPem());

        keys.Verification.Count.ShouldBe(2);
        keys.Verification.ShouldAllBe(k => !k.HasPrivateKey); // a pasted private PEM is reduced to its public half
    }

    [Fact]
    public async Task A_gate_can_verify_with_nothing_but_the_published_jwks()
    {
        string credential = _issuer.Issue(NewTicket(), Now);
        string jwks = JsonSerializer.Serialize(_keys.Jwks, JsonSerializerOptions.Web);
        jwks.ShouldNotContain("\"d\"");

        var gateVerifier = new TicketCredentialVerifier(new JsonWebKeySet(jwks).GetSigningKeys(), _options, _clock);

        (await gateVerifier.VerifyAsync(credential)).IsSuccess.ShouldBeTrue();
    }

    // ---------- the key itself ----------

    [Fact]
    public void Production_without_a_qr_key_refuses_to_start()
    {
        Should.Throw<InvalidOperationException>(() => new QrKeys(
            Options.Create(new QrOptions()),
            new HostingEnvironment { EnvironmentName = Environments.Production },
            NullLogger<QrKeys>.Instance)).Message.ShouldContain("Qr:SigningKeyPem");
    }

    [Fact]
    public void A_bad_retired_key_refuses_to_start_and_names_the_setting()
    {
        Should.Throw<InvalidOperationException>(() => NewKeys(null, "garbage"))
            .Message.ShouldContain("Qr:RetiredPublicKeyPems:0");
    }
}

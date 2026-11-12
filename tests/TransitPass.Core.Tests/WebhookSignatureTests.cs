using System.Text;
using TransitPass.Core.Api.Payments;

namespace TransitPass.Core.Tests;

public sealed class WebhookSignatureTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(new string('k', 40));
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"eventId":"evt_1","amountPoisha":50000}""");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static bool Verify(string? header, byte[]? body = null, byte[]? secret = null) =>
        WebhookSignature.Verify(header, body ?? Body, secret ?? Secret, Now, Tolerance);

    private static string Sign(long t, byte[]? body = null, byte[]? secret = null) =>
        WebhookSignature.Sign(secret ?? Secret, t, body ?? Body);

    [Fact]
    public void A_fresh_signature_over_the_exact_body_verifies() =>
        Verify(Sign(Now.ToUnixTimeSeconds())).ShouldBeTrue();

    [Fact]
    public void Changing_one_byte_of_the_body_breaks_it()
    {
        byte[] tampered = Encoding.UTF8.GetBytes("""{"eventId":"evt_1","amountPoisha":90000}""");
        Verify(Sign(Now.ToUnixTimeSeconds()), body: tampered).ShouldBeFalse();
    }

    [Fact]
    public void Re_serialized_json_with_other_spacing_does_not_verify()
    {
        byte[] spaced = Encoding.UTF8.GetBytes("""{ "eventId": "evt_1", "amountPoisha": 50000 }""");
        Verify(Sign(Now.ToUnixTimeSeconds()), body: spaced).ShouldBeFalse(); // why we keep the raw bytes
    }

    [Fact]
    public void A_different_secret_does_not_verify() =>
        Verify(Sign(Now.ToUnixTimeSeconds(), secret: Encoding.UTF8.GetBytes(new string('x', 40)))).ShouldBeFalse();

    [Theory]
    [InlineData(-301, false)] // captured 5 min 1 s ago: a replay
    [InlineData(-299, true)]
    [InlineData(299, true)]   // small clock drift is fine
    [InlineData(301, false)]
    public void The_timestamp_must_be_within_five_minutes(int offsetSeconds, bool ok) =>
        Verify(Sign(Now.ToUnixTimeSeconds() + offsetSeconds)).ShouldBe(ok);

    [Fact]
    public void Moving_an_old_signature_to_a_new_timestamp_fails()
    {
        string old = Sign(Now.ToUnixTimeSeconds() - 3600);
        string mac = old.Split(",v1=")[1];
        Verify($"t={Now.ToUnixTimeSeconds()},v1={mac}").ShouldBeFalse(); // t is inside the MAC
    }

    [Fact]
    public void During_secret_rotation_any_listed_v1_may_match()
    {
        long t = Now.ToUnixTimeSeconds();
        string right = Sign(t).Split(",v1=")[1];
        Verify($"t={t},v1={new string('0', 64)},v1={right}").ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=1800000000")]                                  // no MAC
    [InlineData("v1=00")]                                         // no timestamp
    [InlineData("t=abc,v1=0000")]                                 // bad timestamp
    [InlineData("t=1800000000,v1=zz")]                            // not hex
    [InlineData("t=1800000000,v1=0011")]                          // too short
    [InlineData("t=-5,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    public void Malformed_headers_are_refused(string? header) => Verify(header).ShouldBeFalse();
}

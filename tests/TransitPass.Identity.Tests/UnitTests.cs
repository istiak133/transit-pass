using TransitPass.Identity.Api.Users;

namespace TransitPass.Identity.Tests;

public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("01711000001", "+8801711000001")]
    [InlineData("8801711000001", "+8801711000001")]
    [InlineData("+8801711000001", "+8801711000001")]
    [InlineData("+880 1711-000001", "+8801711000001")]
    [InlineData("(017) 1100 0001", "+8801711000001")]
    [InlineData("01911222333", "+8801911222333")]
    public void Normalizes_to_e164(string input, string expected)
    {
        PhoneNumber.TryNormalize(input, out var e164).ShouldBeTrue();
        e164.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0171100000")]      // 10 digits
    [InlineData("017110000011")]    // 12 digits
    [InlineData("01211000001")]     // operator digit 2 doesn't exist
    [InlineData("02711000001")]     // landline, not mobile
    [InlineData("+14155550100")]    // not Bangladesh
    [InlineData("01711abc001")]     // letters
    [InlineData("০১৭১১০০০০০১")]     // Bengali digits are rejected, not silently mixed
    public void Rejects_invalid(string? input)
    {
        PhoneNumber.TryNormalize(input, out _).ShouldBeFalse();
    }
}

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_verifies_and_is_not_the_password()
    {
        string hash = PasswordHasher.Hash("correct horse");

        hash.ShouldNotContain("correct horse");
        hash.ShouldStartWith("pbkdf2-sha256$600000$");
        PasswordHasher.Verify("correct horse", hash).ShouldBeTrue();
        PasswordHasher.Verify("correct horsE", hash).ShouldBeFalse();
    }

    [Fact]
    public void Same_password_gets_different_hashes_because_of_salt()
    {
        PasswordHasher.Hash("same-password").ShouldNotBe(PasswordHasher.Hash("same-password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("md5$abc")]
    [InlineData("pbkdf2-sha256$notanumber$AA==$AA==")]
    public void Malformed_stored_hash_does_not_verify(string stored)
    {
        PasswordHasher.Verify("anything", stored).ShouldBeFalse();
    }
}

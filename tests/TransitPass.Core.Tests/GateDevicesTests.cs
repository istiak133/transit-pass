using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TransitPass.Core.Api.Gates;

namespace TransitPass.Core.Tests;

/// <summary>Gate enrollment and authentication on their own: only the hash of a key is configured.</summary>
public sealed class GateDevicesTests
{
    private static readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static GateDevices Enrol(params GateDeviceOptions[] devices) =>
        new(Options.Create(new GateOptions { Devices = [.. devices] }));

    private static GateDeviceOptions Device(string id = "gate-1", string station = "motijheel", string? key = null) =>
        new() { Id = id, Station = station, KeySha256 = GateDevices.HashKey(key ?? Key) };

    [Fact]
    public void HashKey_is_lowercase_hex_sha256_like_openssl()
    {
        // printf '%s' abc | openssl dgst -sha256
        GateDevices.HashKey("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void The_right_key_returns_the_gate_with_its_enrolled_station()
    {
        GateDevices gates = Enrol(Device(), Device("gate-2", "agargaon", Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));

        gates.Count.ShouldBe(2);
        gates.Authenticate("gate-1", Key).ShouldBe(new GateDevice("gate-1", "motijheel"));
    }

    [Theory]
    [InlineData("gate-1", "wrong")]
    [InlineData("gate-1", "")]
    [InlineData("gate-1", null)]
    [InlineData("gate-9", null)]
    [InlineData(null, null)]
    [InlineData("GATE-1", null)] // ids are exact
    public void A_wrong_or_missing_key_or_an_unknown_gate_is_refused(string? gateId, string? key)
    {
        GateDevices gates = Enrol(Device());
        gates.Authenticate(gateId, key ?? (gateId == "gate-9" ? Key : null)).ShouldBeNull();
    }

    [Fact]
    public void Another_gates_key_does_not_open_this_gate()
    {
        string other = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        GateDevices gates = Enrol(Device(), Device("gate-2", "agargaon", other));

        gates.Authenticate("gate-1", other).ShouldBeNull();
        gates.Authenticate("gate-2", other)!.Station.ShouldBe("agargaon");
    }

    [Fact]
    public void A_huge_key_is_refused_without_hashing_it()
    {
        GateDevices gates = Enrol(Device());
        gates.Authenticate("gate-1", Key + new string('x', GateDevices.MaxKeyLength)).ShouldBeNull();
    }

    [Fact]
    public void No_gates_enrolled_means_every_tap_is_refused()
    {
        GateDevices gates = Enrol();
        gates.Count.ShouldBe(0);
        gates.Authenticate("gate-1", Key).ShouldBeNull();
    }

    [Theory]
    [InlineData("", "motijheel", "Id is required")]
    [InlineData("gate-1", "", "Station is required")]
    [InlineData("gate-1", "motijheel", "KeySha256 must be 64 hex")]
    public void A_bad_enrollment_fails_at_startup(string id, string station, string message)
    {
        var device = new GateDeviceOptions
        {
            Id = id,
            Station = station,
            KeySha256 = message.StartsWith("KeySha256", StringComparison.Ordinal) ? "not-a-hash" : GateDevices.HashKey(Key),
        };
        Should.Throw<InvalidOperationException>(() => Enrol(device)).Message.ShouldContain(message);
    }

    [Fact]
    public void A_raw_key_pasted_instead_of_its_hash_fails_at_startup()
    {
        // 64 random bytes as hex = 128 characters: clearly not a SHA-256.
        var device = new GateDeviceOptions { Id = "gate-1", Station = "motijheel", KeySha256 = Key + Key };
        Should.Throw<InvalidOperationException>(() => Enrol(device)).Message.ShouldContain("KeySha256");
    }

    [Fact]
    public void The_same_gate_id_enrolled_twice_fails_at_startup()
    {
        Should.Throw<InvalidOperationException>(() => Enrol(Device(), Device(station: "agargaon")))
            .Message.ShouldContain("enrolled twice");
    }

    [Fact]
    public void A_gate_enrolled_with_the_hash_of_an_empty_key_fails_at_startup()
    {
        // A blank GATE_..._KEY in .env hashes to SHA-256(""). Before this check, such a gate accepted any key longer
        // than MaxKeyLength (an oversized key was hashed as ""), so it must never be enrolled.
        var device = new GateDeviceOptions { Id = "gate-1", Station = "motijheel", KeySha256 = GateDevices.HashKey(string.Empty).ToUpperInvariant() };
        Should.Throw<InvalidOperationException>(() => Enrol(device)).Message.ShouldContain("empty key");
    }

    [Fact]
    public void A_negative_exit_grace_fails_at_startup()
    {
        var options = new GateOptions { Devices = [Device()], ExitGracePeriod = TimeSpan.FromMinutes(-1) };
        Should.Throw<InvalidOperationException>(() => new GateDevices(Options.Create(options))).Message.ShouldContain("ExitGracePeriod");
    }

    [Fact]
    public void A_key_one_character_too_long_is_refused_even_if_it_starts_with_the_real_key()
    {
        string longKey = Key + new string('0', GateDevices.MaxKeyLength - Key.Length + 1);
        GateDevices gates = Enrol(Device(key: longKey[..GateDevices.MaxKeyLength]), Device("gate-2", "agargaon", longKey));

        gates.Authenticate("gate-1", longKey[..GateDevices.MaxKeyLength])!.Id.ShouldBe("gate-1"); // exactly the limit: fine
        gates.Authenticate("gate-2", longKey).ShouldBeNull(); // one over: refused, even with the enrolled key
    }
}

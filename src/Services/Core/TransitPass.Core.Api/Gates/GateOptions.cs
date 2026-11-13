using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TransitPass.Core.Api.Gates;

/// <summary>Settings under "Gate".</summary>
public sealed class GateOptions
{
    public const string Section = "Gate";

    /// <summary>The header a gate device sends its secret key in.</summary>
    public const string KeyHeader = "X-Gate-Key";

    /// <summary>The enrolled gate devices. Only the SHA-256 of each key is configured, never the key itself.</summary>
    public List<GateDeviceOptions> Devices { get; set; } = [];

    /// <summary>
    /// How long after its ticket's service day a QR still opens an EXIT gate. Someone who entered at 23:50 must be
    /// able to leave at 00:10. Entries get no grace at all.
    /// </summary>
    public TimeSpan ExitGracePeriod { get; set; } = TimeSpan.FromHours(3);
}

/// <summary>One enrolled gate: its id, the station it stands in, and the hex SHA-256 of its secret key.</summary>
public sealed class GateDeviceOptions
{
    public string? Id { get; set; }

    public string? Station { get; set; }

    public string? KeySha256 { get; set; }
}

/// <summary>An authenticated gate. Its station comes from enrollment, never from what the request claims.</summary>
public sealed record GateDevice(string Id, string Station);

/// <summary>
/// The enrolled gates, loaded once at startup (a bad entry fails fast). A gate proves itself with a long random
/// key; we keep only its SHA-256. A leaked config or database therefore holds no usable key, and because the key
/// is 256 random bits (not a human password) a fast hash is enough: there is nothing to brute-force.
/// </summary>
public sealed class GateDevices
{
    internal const int MaxIdLength = 64;
    internal const int MaxKeyLength = 256;

    private readonly FrozenDictionary<string, (GateDevice Device, byte[] KeyHash)> _byId;

    // Compared against when the gate id is unknown, so an unknown gate costs the same work as a wrong key.
    private readonly byte[] _dummyHash = RandomNumberGenerator.GetBytes(SHA256.HashSizeInBytes);

    public GateDevices(IOptions<GateOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<string, (GateDevice, byte[])> devices = new(StringComparer.Ordinal);
        for (int i = 0; i < options.Value.Devices.Count; i++)
        {
            GateDeviceOptions d = options.Value.Devices[i];
            string where = $"Gate:Devices:{i}";
            if (string.IsNullOrWhiteSpace(d.Id) || d.Id.Length > MaxIdLength)
            {
                throw new InvalidOperationException($"{where}:Id is required (at most {MaxIdLength} characters).");
            }

            if (string.IsNullOrWhiteSpace(d.Station) || d.Station.Length > 64)
            {
                throw new InvalidOperationException($"{where}:Station is required.");
            }

            if (d.KeySha256 is not { Length: SHA256.HashSizeInBytes * 2 } || !IsHex(d.KeySha256))
            {
                throw new InvalidOperationException($"{where}:KeySha256 must be 64 hex characters (the SHA-256 of the gate's key).");
            }

            if (!devices.TryAdd(d.Id, (new GateDevice(d.Id, d.Station), Convert.FromHexString(d.KeySha256))))
            {
                throw new InvalidOperationException($"{where}:Id '{d.Id}' is enrolled twice.");
            }
        }

        _byId = devices.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int Count => _byId.Count;

    /// <summary>
    /// The gate, if <paramref name="gateId"/> is enrolled and <paramref name="key"/> is its key; otherwise null.
    /// O(1) lookup, then one SHA-256 and a constant-time compare, also for an unknown id.
    /// </summary>
    public GateDevice? Authenticate(string? gateId, string? key)
    {
        byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(key is { Length: <= MaxKeyLength } ? key : string.Empty));
        (GateDevice Device, byte[] KeyHash) entry = default;
        bool known = gateId is not null && _byId.TryGetValue(gateId, out entry);
        bool match = CryptographicOperations.FixedTimeEquals(given, known ? entry.KeyHash : _dummyHash);
        return known && match && !string.IsNullOrEmpty(key) ? entry.Device : null;
    }

    /// <summary>The hex SHA-256 to configure for a key (what deploy/dev-secrets.sh computes with openssl).</summary>
    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static bool IsHex(string s) => s.All(Uri.IsHexDigit);
}

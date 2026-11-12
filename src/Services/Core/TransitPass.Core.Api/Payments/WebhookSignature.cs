using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TransitPass.Core.Api.Payments;

/// <summary>
/// Signs and verifies <c>Payment-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256&gt;</c>.
/// The MAC covers <c>"{t}." + raw body bytes</c>: the exact bytes received, never re-serialized JSON,
/// because re-serializing can reorder keys or change spacing and break an honest signature.
/// Putting <c>t</c> inside the MAC means an attacker can't refresh an old webhook's timestamp.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "Payment-Signature";
    private const int MacLength = 32; // SHA-256

    /// <summary>The header value for a body (what the provider, the tests and deploy/fake-payment.sh compute).</summary>
    public static string Sign(byte[] secret, long unixSeconds, ReadOnlySpan<byte> body) =>
        $"t={unixSeconds.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(Mac(secret, unixSeconds, body))}";

    /// <summary>
    /// True only for a well-formed header, a timestamp within <paramref name="tolerance"/> of now, and a MAC
    /// that matches in constant time. Several v1 values are allowed (the provider sends two during a secret rotation).
    /// </summary>
    public static bool Verify(string? header, ReadOnlySpan<byte> body, byte[] secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header) || header.Length > 512)
        {
            return false;
        }

        long? timestamp = null;
        List<string> macs = [];
        foreach (string part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("t=", StringComparison.Ordinal)
                && long.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out long t))
            {
                timestamp = t;
            }
            else if (part.StartsWith("v1=", StringComparison.Ordinal))
            {
                macs.Add(part[3..]);
            }
        }

        if (timestamp is not long ts || macs.Count == 0)
        {
            return false;
        }

        // Replay window. Checked before the MAC is even computed: cheap, and the MAC protects ts anyway.
        long age = now.ToUnixTimeSeconds() - ts;
        if (Math.Abs(age) > (long)tolerance.TotalSeconds)
        {
            return false;
        }

        byte[] expected = Mac(secret, ts, body);
        Span<byte> given = stackalloc byte[MacLength];
        foreach (string hex in macs)
        {
            // FixedTimeEquals looks at every byte whatever the first mismatch is, so response time
            // tells an attacker nothing about how many leading bytes they guessed right.
            if (hex.Length == MacLength * 2
                && Convert.FromHexString(hex, given, out _, out int written) == System.Buffers.OperationStatus.Done
                && written == MacLength
                && CryptographicOperations.FixedTimeEquals(given, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Mac(byte[] secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, secret);
        hmac.AppendData(Encoding.ASCII.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture) + "."));
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }
}

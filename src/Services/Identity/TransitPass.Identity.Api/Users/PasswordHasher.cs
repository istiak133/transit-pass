using System.Globalization;
using System.Security.Cryptography;

namespace TransitPass.Identity.Api.Users;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a random 16-byte salt per password.
/// Stored format: <c>pbkdf2-sha256$iterations$salt(base64)$hash(base64)</c>. The format carries its own
/// parameters, so the iteration count can be raised later without breaking existing hashes.
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP's 2023+ recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    /// <summary>Hashes a password with a fresh random salt.</summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>True if the password matches the stored hash. Compares in constant time.</summary>
    public static bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(stored);

        string[] parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations))
        {
            return false;
        }

        byte[] salt = Convert.FromBase64String(parts[2]);
        byte[] expected = Convert.FromBase64String(parts[3]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant time: the comparison takes the same time however many bytes match,
        // so an attacker can't learn the hash byte by byte from response timings.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

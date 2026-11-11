using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TransitPass.Identity.Api.Users;

/// <summary>
/// Normalizes Bangladeshi mobile numbers to E.164 (<c>+8801XXXXXXXXX</c>), so that
/// "01711000001", "8801711000001" and "+880 1711-000001" are recognized as the same phone.
/// </summary>
public static partial class PhoneNumber
{
    /// <summary>Returns true and the E.164 form if the input is a valid Bangladeshi mobile number.</summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? e164)
    {
        e164 = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 30)
        {
            return false;
        }

        // Keep digits only; spaces, dashes and the leading + are formatting.
        Span<char> digits = stackalloc char[input.Length];
        int n = 0;
        foreach (char c in input)
        {
            if (char.IsAsciiDigit(c))
            {
                digits[n++] = c;
            }
            else if (c is not (' ' or '-' or '+' or '(' or ')'))
            {
                return false; // letters or other symbols: not a phone number
            }
        }

        string d = new(digits[..n]);
        string national = d switch
        {
            _ when d.Length == 13 && d.StartsWith("880", StringComparison.Ordinal) => d[3..], // 8801XXXXXXXXX
            _ when d.Length == 11 && d.StartsWith('0') => d[1..],                              // 01XXXXXXXXX
            _ => string.Empty,
        };

        // Bangladeshi mobile: 1, then operator digit 3–9, then 8 digits.
        if (!MobilePattern().IsMatch(national))
        {
            return false;
        }

        e164 = "+880" + national;
        return true;
    }

    [GeneratedRegex("^1[3-9][0-9]{8}$")]
    private static partial Regex MobilePattern();
}

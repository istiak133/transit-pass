using System.Buffers.Text;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.BuildingBlocks.Pagination;

/// <summary>
/// An opaque keyset-pagination cursor. It encodes the id of the last item on the previous page.
/// The next page is <c>WHERE id &lt; @cursor ORDER BY id DESC LIMIT @n</c>, which an index answers in O(log n + n_page),
/// no matter how deep the page is. OFFSET would scan and discard every earlier row: O(offset + n_page).
/// </summary>
/// <remarks>
/// Layout: one version byte, then the 16 bytes of the UUIDv7, base64url encoded (23 characters, URL safe).
/// Clients must treat it as opaque. The version byte lets the format change later without breaking old links.
/// </remarks>
public static class Cursor
{
    private const byte Version = 1;
    private const int PayloadLength = 17;

    /// <summary>Encodes the id of the last item returned.</summary>
    public static string Encode(Guid lastId)
    {
        Span<byte> buffer = stackalloc byte[PayloadLength];
        buffer[0] = Version;
        lastId.TryWriteBytes(buffer[1..], bigEndian: true, out _);
        return Base64Url.EncodeToString(buffer);
    }

    /// <summary>Decodes a cursor from the query string. Anything malformed is a validation error, never a 500.</summary>
    public static Result<Guid> Decode(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        Span<byte> buffer = stackalloc byte[PayloadLength + 1];
        if (cursor.Length > 32
            || !Base64Url.IsValid(cursor)
            || !Base64Url.TryDecodeFromChars(cursor, buffer, out int written)
            || written != PayloadLength
            || buffer[0] != Version)
        {
            return Error.Validation("pagination.invalid_cursor", "The cursor is not valid.");
        }

        return new Guid(buffer[1..PayloadLength], bigEndian: true);
    }
}

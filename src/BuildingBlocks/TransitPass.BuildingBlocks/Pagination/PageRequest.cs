namespace TransitPass.BuildingBlocks.Pagination;

/// <summary>A page request from the query string: <c>?after=&lt;cursor&gt;&amp;limit=20</c>.</summary>
/// <param name="After">The cursor from the previous page, or null for the first page.</param>
/// <param name="Limit">How many items. It is clamped to [1, <see cref="MaxLimit"/>], so one call can never ask for a million rows.</param>
public sealed record PageRequest(string? After, int Limit)
{
    /// <summary>Used when the client does not send a limit.</summary>
    public const int DefaultLimit = 20;

    /// <summary>The largest page the API will return.</summary>
    public const int MaxLimit = 100;

    /// <summary>Builds a request with a clamped limit.</summary>
    public static PageRequest Create(string? after, int? limit) =>
        new(string.IsNullOrWhiteSpace(after) ? null : after, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit));

    /// <summary>
    /// How many rows to fetch: one more than the limit. If the extra row comes back, a next page exists.
    /// This answers "is there more?" without a <c>COUNT(*)</c>, which would be O(n) on every request.
    /// </summary>
    public int FetchSize => Limit + 1;
}

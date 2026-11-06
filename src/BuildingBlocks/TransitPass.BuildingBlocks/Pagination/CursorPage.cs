namespace TransitPass.BuildingBlocks.Pagination;

/// <summary>One page of results plus the cursor for the next one (null on the last page).</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    /// <summary>
    /// Builds a page from rows fetched with <see cref="PageRequest.FetchSize"/> (limit + 1).
    /// O(limit) time and space: it copies at most <c>limit</c> items.
    /// </summary>
    public static CursorPage<T> From(IReadOnlyList<T> fetched, PageRequest request, Func<T, Guid> idOf)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(idOf);

        if (fetched.Count <= request.Limit)
        {
            return new CursorPage<T>(fetched, null);
        }

        var items = new T[request.Limit];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = fetched[i];
        }

        return new CursorPage<T>(items, Cursor.Encode(idOf(items[^1])));
    }
}

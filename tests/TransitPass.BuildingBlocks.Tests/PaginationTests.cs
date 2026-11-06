using TransitPass.BuildingBlocks.Pagination;

namespace TransitPass.BuildingBlocks.Tests;

public sealed class PaginationTests
{
    [Fact]
    public void Cursor_round_trips()
    {
        var id = Guid.CreateVersion7();

        string cursor = Cursor.Encode(id);
        var decoded = Cursor.Decode(cursor);

        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.ShouldBe(id);
        cursor.ShouldNotContain("+");
        cursor.ShouldNotContain("/");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AQ")]
    [InlineData("%%%%")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_cursor_is_a_validation_error(string cursor)
    {
        var decoded = Cursor.Decode(cursor);

        decoded.IsSuccess.ShouldBeFalse();
        decoded.Error!.Code.ShouldBe("pagination.invalid_cursor");
    }

    [Fact]
    public void Cursor_with_unknown_version_is_rejected()
    {
        string valid = Cursor.Encode(Guid.CreateVersion7());
        // First base64url character carries the top 6 bits of the version byte. 'B' makes the version byte 5, not 1.
        string otherVersion = "B" + valid[1..];

        Cursor.Decode(otherVersion).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    [InlineData(10_000, 100)]
    public void Limit_is_clamped(int? requested, int expected) =>
        PageRequest.Create(null, requested).Limit.ShouldBe(expected);

    [Fact]
    public void Extra_row_means_there_is_a_next_page()
    {
        var request = PageRequest.Create(null, 2);
        var rows = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() }; // limit + 1

        var page = CursorPage<Guid>.From(rows, request, id => id);

        page.Items.Count.ShouldBe(2);
        page.NextCursor.ShouldNotBeNull();
        Cursor.Decode(page.NextCursor!).Value.ShouldBe(rows[1]);
    }

    [Fact]
    public void No_extra_row_means_last_page()
    {
        var request = PageRequest.Create(null, 2);
        var rows = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };

        var page = CursorPage<Guid>.From(rows, request, id => id);

        page.Items.Count.ShouldBe(2);
        page.NextCursor.ShouldBeNull();
    }
}

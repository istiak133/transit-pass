using TransitPass.BuildingBlocks.Hosting;

namespace TransitPass.BuildingBlocks.Tests;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData("abc-123_XYZ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234", false)] // 65 chars
    public void Only_short_safe_ids_are_kept(string? value, bool expected) =>
        CorrelationIdMiddleware.IsSafe(value).ShouldBe(expected);
}

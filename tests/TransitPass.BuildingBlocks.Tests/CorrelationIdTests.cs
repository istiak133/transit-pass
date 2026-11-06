using TransitPass.BuildingBlocks.Hosting;

namespace TransitPass.BuildingBlocks.Tests;

public sealed class CorrelationIdTests
{
    [Fact]
    public void Valid_uuid_is_kept()
    {
        string id = Guid.CreateVersion7().ToString("N");

        CorrelationIdMiddleware.Normalize(id).ShouldBe(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("client-abc_123")]
    [InlineData("line\nbreak")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("0193f8a2-7c1e-7000-8000-000000000000")] // dashed form is not the N format
    public void Anything_else_is_replaced_with_a_fresh_uuid(string? incoming)
    {
        string result = CorrelationIdMiddleware.Normalize(incoming);

        result.ShouldNotBe(incoming);
        result.Length.ShouldBe(32);
        Guid.TryParseExact(result, "N", out _).ShouldBeTrue();
    }
}

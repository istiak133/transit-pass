using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.BuildingBlocks.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Implicit_value_becomes_success()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Implicit_error_becomes_failure_and_value_access_throws()
    {
        Result<int> result = Error.NotFound("ticket.not_found", "Ticket not found.");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("ticket.not_found");
        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.BusinessRule, 422)]
    public void Error_type_maps_to_http_status(ErrorType type, int expected) =>
        type.ToStatusCode().ShouldBe(expected);
}

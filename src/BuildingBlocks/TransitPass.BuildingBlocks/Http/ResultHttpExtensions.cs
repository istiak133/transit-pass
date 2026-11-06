using Microsoft.AspNetCore.Http;
using TransitPass.BuildingBlocks.Results;

namespace TransitPass.BuildingBlocks.Http;

/// <summary>Turns <see cref="Result"/> values into HTTP responses at the endpoint edge.</summary>
public static class ResultHttpExtensions
{
    /// <summary>Maps an error category to its HTTP status code.</summary>
    internal static int ToStatusCode(this ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// An RFC 9457 problem-details response for the error. The stable <c>code</c> goes in an extension member.
    /// <c>traceId</c> is added for every problem response by the problem-details customization in hosting defaults.
    /// </summary>
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return TypedResults.Problem(
            statusCode: error.Type.ToStatusCode(),
            title: error.Message,
            type: $"https://transitpass.dev/errors/{error.Code}",
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    /// <summary>200 with the value on success, otherwise the problem response.</summary>
    public static IResult ToHttp<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

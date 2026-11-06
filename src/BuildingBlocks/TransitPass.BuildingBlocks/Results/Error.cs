namespace TransitPass.BuildingBlocks.Results;

/// <summary>The category of an expected failure. It decides the HTTP status at the API edge.</summary>
public enum ErrorType
{
    /// <summary>The request is malformed (400).</summary>
    Validation,

    /// <summary>The caller is not authenticated (401).</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but not allowed (403).</summary>
    Forbidden,

    /// <summary>The resource does not exist (404).</summary>
    NotFound,

    /// <summary>The request conflicts with the current state, for example a duplicate in flight (409).</summary>
    Conflict,

    /// <summary>The request is well formed but breaks a business rule, for example insufficient funds (422).</summary>
    BusinessRule,
}

/// <summary>
/// An expected failure, returned as a value instead of thrown.
/// <see cref="Code"/> is stable and machine-readable (for example <c>wallet.insufficient_funds</c>), so clients can branch on it.
/// <see cref="Message"/> is for humans and may change.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    /// <summary>Creates a validation error.</summary>
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    /// <summary>Creates a not-found error.</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    /// <summary>Creates a conflict error.</summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    /// <summary>Creates an unauthorized error.</summary>
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    /// <summary>Creates a forbidden error.</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    /// <summary>Creates a business-rule error.</summary>
    public static Error BusinessRule(string code, string message) => new(code, message, ErrorType.BusinessRule);
}

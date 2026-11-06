using System.Diagnostics.CodeAnalysis;

namespace TransitPass.BuildingBlocks.Results;

/// <summary>
/// The outcome of an operation that can fail in an expected way.
/// Exceptions stay for bugs and broken infrastructure. "Balance too low" is not exceptional, so it is a value.
/// </summary>
public class Result
{
    /// <summary>Creates a result. Exactly one of success or an error must hold.</summary>
    protected Result(Error? error) => Error = error;

    /// <summary>The failure, or <c>null</c> on success.</summary>
    public Error? Error { get; }

    /// <summary>True when the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>A successful result with no value.</summary>
    public static Result Success() => new(null);

    /// <summary>A failed result.</summary>
    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    /// <summary>Lets a handler write <c>return Error.NotFound(...)</c>.</summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>The outcome of an operation that returns a <typeparamref name="T"/> on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error)
        : base(error) => _value = value;

    /// <summary>The value. Reading it from a failed result is a bug, so it throws.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    /// <summary>A successful result.</summary>
    public static Result<T> Success(T value) => new(value, null);

    /// <summary>A failed result.</summary>
    public static new Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(default, error);
    }

    /// <summary>Lets a handler write <c>return ticket;</c>.</summary>
    public static implicit operator Result<T>(T value) => Success(value);

    /// <summary>Lets a handler write <c>return Error.NotFound(...)</c>.</summary>
    public static implicit operator Result<T>(Error error) => Failure(error);
}

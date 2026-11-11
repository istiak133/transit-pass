using Microsoft.EntityFrameworkCore;
using Npgsql;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Identity.Api.Data;

namespace TransitPass.Identity.Api.Users;

/// <summary>Sign-up request body. Fields are nullable so a missing field becomes a 400 with a clear code, not a parse error.</summary>
public sealed record RegisterUserRequest(string? Phone, string? Name, string? Password);

/// <summary>What the API returns about a user. Never includes the password hash.</summary>
public sealed record UserResponse(Guid Id, string Phone, string Name, DateTimeOffset CreatedAt);

/// <summary>User endpoints under /api/v1/identity/users.</summary>
public static class RegisterEndpoints
{
    internal const int MinPasswordLength = 8;
    internal const int MaxPasswordLength = 128; // caps PBKDF2 work per request (DoS guard)

    /// <summary>Maps the user routes.</summary>
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/v1/identity/users → 201 Created + Location, 400 invalid, 409 phone taken
        app.MapPost("/api/v1/identity/users", async (RegisterUserRequest body, IdentityDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var result = await RegisterAsync(body, db, clock, ct);
            return result.IsSuccess
                ? TypedResults.Created($"/api/v1/identity/users/{result.Value.Id}", result.Value)
                : result.Error.ToProblem();
        });

        return app;
    }

    internal static async Task<Result<UserResponse>> RegisterAsync(
        RegisterUserRequest body, IdentityDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // 1. Validate and normalize. Cheap checks first, before the expensive hash.
        if (!PhoneNumber.TryNormalize(body.Phone, out var phone))
        {
            return Error.Validation("user.invalid_phone", "Phone must be a Bangladeshi mobile number, e.g. 01711000001.");
        }

        string name = body.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            return Error.Validation("user.invalid_name", "Name is required and must be at most 100 characters.");
        }

        if (body.Password is null || body.Password.Length is < MinPasswordLength or > MaxPasswordLength)
        {
            return Error.Validation("user.invalid_password",
                $"Password must be {MinPasswordLength}–{MaxPasswordLength} characters.");
        }

        // 2. Hash (deliberately slow: ~600k PBKDF2 rounds).
        var user = new User
        {
            Id = Guid.CreateVersion7(clock.GetUtcNow()),
            Phone = phone,
            Name = name,
            PasswordHash = PasswordHasher.Hash(body.Password),
            CreatedAt = clock.GetUtcNow(),
        };

        // 3. Insert. No "SELECT first, then INSERT": two requests could both see "not taken".
        //    The UNIQUE index decides, atomically, and we translate its error.
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_users_phone",
        })
        {
            return Error.Conflict("user.phone_taken", "An account with this phone already exists.");
        }

        return new UserResponse(user.Id, user.Phone, user.Name, user.CreatedAt);
    }
}

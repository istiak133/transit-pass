namespace TransitPass.Identity.Api.Users;

/// <summary>A registered passenger. One row of the <c>users</c> table.</summary>
public sealed class User
{
    /// <summary>UUIDv7: unique without coordination and time-ordered, so B+ tree inserts stay at the right edge.</summary>
    public required Guid Id { get; init; }

    /// <summary>E.164 format, for example <c>+8801711000001</c>. Unique.</summary>
    public required string Phone { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; set; }

    /// <summary>Self-describing PBKDF2 hash, never the password itself. See <see cref="PasswordHasher"/>.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>When the account was created (UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

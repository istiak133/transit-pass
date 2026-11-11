namespace TransitPass.Network.Api.Stations;

/// <summary>A metro station. EF Core maps one instance to one row of the <c>stations</c> table.</summary>
public sealed class Station
{
    /// <summary>Readable, stable key such as <c>agargaon</c>. Also used in URLs.</summary>
    public required string Id { get; init; }

    /// <summary>Display name such as <c>Agargaon</c>.</summary>
    public required string Name { get; set; }
}

/// <summary>What the API returns. Kept separate from the entity so the table can change without breaking clients.</summary>
public sealed record StationResponse(string Id, string Name);

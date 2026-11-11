namespace TransitPass.Network.Api.Segments;

/// <summary>
/// One stretch of track between two neighbouring stations. Track runs both ways, so each stretch
/// is stored once, with the two ids in alphabetical order (<see cref="StationA"/> &lt; <see cref="StationB"/>).
/// </summary>
public sealed class Segment
{
    /// <summary>The alphabetically smaller station id.</summary>
    public required string StationA { get; init; }

    /// <summary>The alphabetically larger station id.</summary>
    public required string StationB { get; init; }

    /// <summary>Track length in metres. Whole numbers, so there's no rounding.</summary>
    public required int DistanceM { get; set; }
}

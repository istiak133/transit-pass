namespace TransitPass.Network.Api.Fares;

/// <summary>Turns a travel distance into a price. A strategy, so pricing can change without touching routing.</summary>
public interface IFarePolicy
{
    /// <summary>The fare in poisha (৳1 = 100 poisha) for a trip of this many metres.</summary>
    long FareFor(long metres);
}

/// <summary>
/// Distance slabs: "up to 2 km costs ৳20, up to 5 km costs ৳30, …".
/// The right slab is found with a binary search over the slab limits, O(log s).
/// </summary>
public sealed class SlabFarePolicy : IFarePolicy
{
    // Sorted by limit. The last slab covers any longer trip.
    private static readonly (long UpToMetres, long Poisha)[] Slabs =
    [
        (2_000, 2_000),       // ≤ 2 km  → ৳20
        (5_000, 3_000),       // ≤ 5 km  → ৳30
        (8_000, 4_000),       // ≤ 8 km  → ৳40
        (12_000, 6_000),      // ≤ 12 km → ৳60
        (16_000, 8_000),      // ≤ 16 km → ৳80
        (long.MaxValue, 10_000), // longer → ৳100
    ];

    private static readonly long[] Limits = Slabs.Select(s => s.UpToMetres).ToArray();

    /// <inheritdoc />
    public long FareFor(long metres)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metres);

        // BinarySearch returns the index if found exactly, otherwise the bitwise complement of the
        // index of the first larger element, which is exactly the slab this distance falls into.
        int i = Array.BinarySearch(Limits, metres);
        return Slabs[i >= 0 ? i : ~i].Poisha;
    }
}

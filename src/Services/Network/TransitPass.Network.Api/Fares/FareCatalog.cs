using Microsoft.EntityFrameworkCore;
using TransitPass.Network.Api.Data;

namespace TransitPass.Network.Api.Fares;

/// <summary>
/// Holds the precomputed <see cref="RouteGraph"/>. The network changes rarely, so it is built once from the
/// database and then served from memory. A restart picks up track changes.
/// </summary>
public sealed class FareCatalog(IServiceScopeFactory scopes) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RouteGraph? _graph;

    /// <summary>The graph, built on first use.</summary>
    public async Task<RouteGraph> GetGraphAsync(CancellationToken ct)
    {
        // Fast path: already built. A plain read is safe because the reference is only ever set once, fully built.
        if (Volatile.Read(ref _graph) is { } ready)
        {
            return ready;
        }

        // Slow path: one caller builds, everyone else waits and then reuses the result.
        await _gate.WaitAsync(ct);
        try
        {
            if (_graph is null)
            {
                // If this throws (database down), _graph stays null, so the next request retries
                // instead of caching the failure forever.
                Volatile.Write(ref _graph, await LoadAsync(ct));
            }

            return _graph!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RouteGraph> LoadAsync(CancellationToken ct)
    {
        // The catalog is a singleton but DbContext is scoped, so borrow a short-lived scope.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkDbContext>();

        var stations = await db.Stations.AsNoTracking().Select(s => s.Id).ToListAsync(ct);
        var segments = await db.Segments.AsNoTracking()
            .Select(s => new { s.StationA, s.StationB, s.DistanceM })
            .ToListAsync(ct);

        return RouteGraph.Build(stations, segments.Select(s => (s.StationA, s.StationB, s.DistanceM)));
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}

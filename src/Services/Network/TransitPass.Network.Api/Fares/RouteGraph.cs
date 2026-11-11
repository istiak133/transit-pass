namespace TransitPass.Network.Api.Fares;

/// <summary>
/// The metro network as a weighted, undirected graph, with the shortest distance between every pair of
/// stations computed once up front.
/// <para>
/// Build: Dijkstra with a binary heap from every station, O(V · (V + E) log V) time, O(V²) space.
/// Query: <see cref="TryGetDistance"/> is two dictionary lookups plus one array read, O(1).
/// </para>
/// </summary>
public sealed class RouteGraph
{
    /// <summary>Marks a pair with no path between them.</summary>
    private const long Unreachable = long.MaxValue;

    private readonly Dictionary<string, int> _indexOf;
    private readonly long[] _distance; // flat V×V matrix: _distance[from * V + to]
    private readonly int _count;

    private RouteGraph(Dictionary<string, int> indexOf, long[] distance)
    {
        _indexOf = indexOf;
        _distance = distance;
        _count = indexOf.Count;
    }

    /// <summary>Number of stations in the graph.</summary>
    public int StationCount => _count;

    /// <summary>
    /// Builds the graph and precomputes all shortest distances.
    /// Edges are undirected: each (a, b, metres) can be travelled both ways.
    /// </summary>
    public static RouteGraph Build(IEnumerable<string> stationIds, IEnumerable<(string A, string B, int Metres)> edges)
    {
        ArgumentNullException.ThrowIfNull(stationIds);
        ArgumentNullException.ThrowIfNull(edges);

        // 1. Give every station a number 0..V-1, so we can use arrays instead of dictionaries in the hot loop.
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in stationIds)
        {
            indexOf.TryAdd(id, indexOf.Count);
        }

        int v = indexOf.Count;

        // 2. Adjacency list: for each station, its neighbours and the distance to each. O(V + E) space.
        var neighbours = new List<(int To, int Metres)>[v];
        for (int i = 0; i < v; i++)
        {
            neighbours[i] = [];
        }

        foreach (var (a, b, metres) in edges)
        {
            if (metres <= 0)
            {
                throw new ArgumentException($"Segment {a}–{b} has a non-positive distance.", nameof(edges));
            }

            if (!indexOf.TryGetValue(a, out int ia) || !indexOf.TryGetValue(b, out int ib))
            {
                throw new ArgumentException($"Segment {a}–{b} refers to an unknown station.", nameof(edges));
            }

            neighbours[ia].Add((ib, metres));
            neighbours[ib].Add((ia, metres)); // undirected
        }

        // 3. One Dijkstra run per source station fills one row of the matrix.
        var distance = new long[v * v];
        for (int source = 0; source < v; source++)
        {
            Dijkstra(source, neighbours, distance.AsSpan(source * v, v));
        }

        return new RouteGraph(indexOf, distance);
    }

    /// <summary>Shortest track distance in metres, or false if a station is unknown or unreachable.</summary>
    public bool TryGetDistance(string from, string to, out long metres)
    {
        metres = 0;
        if (!_indexOf.TryGetValue(from, out int f) || !_indexOf.TryGetValue(to, out int t))
        {
            return false;
        }

        metres = _distance[(f * _count) + t];
        return metres != Unreachable;
    }

    /// <summary>True if the station id exists in the graph.</summary>
    public bool Contains(string stationId) => _indexOf.ContainsKey(stationId);

    /// <summary>
    /// Classic Dijkstra with a binary min-heap and "lazy deletion": instead of decreasing a key inside the heap,
    /// we push a new entry and skip stale ones when they come out. Valid because all weights are positive.
    /// </summary>
    private static void Dijkstra(int source, List<(int To, int Metres)>[] neighbours, Span<long> dist)
    {
        dist.Fill(Unreachable);
        dist[source] = 0;

        var heap = new PriorityQueue<int, long>();
        heap.Enqueue(source, 0);

        while (heap.TryDequeue(out int u, out long d))
        {
            if (d > dist[u])
            {
                continue; // stale entry: we already found a shorter way to u
            }

            foreach (var (to, metres) in neighbours[u])
            {
                long candidate = d + metres;
                if (candidate < dist[to])
                {
                    dist[to] = candidate;
                    heap.Enqueue(to, candidate);
                }
            }
        }
    }
}

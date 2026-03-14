namespace DotNetGraphScanner.Graph;

/// <summary>
/// Central in-memory graph built during analysis. Thread-unsafe – populate from a
/// single thread, then hand off to exporters.
/// </summary>
public sealed class GraphModel
{
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();
    private int _edgeSeq;

    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;

    // ── Node helpers ─────────────────────────────────────────────────────────

    public GraphNode AddNode(string id, string label, NodeKind kind,
                             bool isEntryPoint = false,
                             Dictionary<string, string>? meta = null)
    {
        if (_nodes.TryGetValue(id, out var existing))
        {
            if (isEntryPoint) existing.IsEntryPoint = true;
            if (meta is not null)
            {
                foreach (var (key, value) in meta)
                    existing.Meta[key] = value;
            }
            return existing;
        }

        var node = new GraphNode
        {
            Id = id,
            Label = label,
            Kind = kind,
            IsEntryPoint = isEntryPoint,
            Meta = meta ?? new()
        };
        _nodes[id] = node;
        return node;
    }

    public bool HasNode(string id) => _nodes.ContainsKey(id);

    // ── Edge helpers ──────────────────────────────────────────────────────────

    public GraphEdge AddEdge(string sourceId, string targetId, EdgeKind kind,
                             Dictionary<string, string>? meta = null)
    {
        // Deduplicate same-kind edges between same pair
        var duplicate = _edges.FirstOrDefault(e =>
            e.SourceId == sourceId && e.TargetId == targetId && e.Kind == kind);
        if (duplicate is not null) return duplicate;

        var edge = new GraphEdge
        {
            Id = $"e{++_edgeSeq}",
            SourceId = sourceId,
            TargetId = targetId,
            Kind = kind,
            Meta = meta ?? new()
        };
        _edges.Add(edge);
        return edge;
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public void PrintStats()
    {
        Console.WriteLine($"  Nodes : {_nodes.Count}");
        Console.WriteLine($"  Edges : {_edges.Count}");

        foreach (var g in _nodes.Values.GroupBy(n => n.Kind).OrderBy(g => g.Key.ToString()))
            Console.WriteLine($"    {g.Key,-20} {g.Count()}");
    }
}

using DotNetGraphScanner.Graph;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Extracts cross-API metadata from a single API's <see cref="GraphModel"/>.
/// Runs BFS over the internal call graph (crossing virtual dispatch) to map
/// entry points to the outbound [ApiCall] methods they can reach.
/// The result is suitable for pushing to <see cref="Store.Neo4jGraphStore"/>
/// or for direct in-process aggregation.
/// </summary>
public static class CrossApiExtractor
{
    public static SingleApiCrossInfo Extract(GraphModel graph, string apiName)
    {
        // ── Entry points (HTTP endpoints only) ────────────────────────────────
        var entryPoints = new List<EntrypointInfo>();
        var seenEpIds   = new HashSet<string>();

        foreach (var edge in graph.Edges.Where(e => e.Kind == EdgeKind.EntryPoint))
        {
            if (!graph.Nodes.TryGetValue(edge.TargetId, out var node)) continue;

            node.Meta.TryGetValue("httpMethod",    out var verb);
            node.Meta.TryGetValue("routeTemplate", out var route);

            // Skip non-HTTP entry points (static Main, top-level program, etc.)
            if (verb is null && route is null) continue;
            if (!seenEpIds.Add(node.Id)) continue;

            var httpMethod = verb  ?? "HTTP";
            var routePath  = route ?? node.Label;

            entryPoints.Add(new EntrypointInfo(
                NodeId:     node.Id,
                ApiName:    apiName,
                HttpMethod: httpMethod,
                Route:      routePath,
                Label:      $"{httpMethod} {routePath}"));
        }

        // ── Outbound API call nodes ───────────────────────────────────────────
        var outboundCalls = new List<ApiCallInfo>();
        var seenCallIds   = new HashSet<string>();

        foreach (var edge in graph.Edges.Where(e => e.Kind == EdgeKind.ExternalApiCall))
        {
            if (!graph.Nodes.TryGetValue(edge.TargetId, out var node)) continue;
            if (!node.Meta.TryGetValue("targetApi",   out var targetApi))   continue;
            if (!node.Meta.TryGetValue("targetRoute", out var targetRoute)) continue;
            if (!seenCallIds.Add(node.Id)) continue;

            outboundCalls.Add(new ApiCallInfo(
                NodeId:      node.Id,
                OwnerApi:    apiName,
                TargetApi:   targetApi,
                TargetRoute: targetRoute,
                Label:       $"{apiName} → {targetApi}: {targetRoute}"));
        }

        // ── BFS reachability: entry point → outbound call nodes ───────────────
        // Build Calls adjacency augmented with virtual dispatch edges so the BFS
        // crosses interface → implementation boundaries transparently.
        var successors = graph.Edges
            .Where(e => e.Kind == EdgeKind.Calls)
            .GroupBy(e => e.SourceId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.TargetId).ToHashSet());

        foreach (var (ifaceMethodId, implIds) in BuildVirtualDispatchMap(graph))
        {
            if (!successors.ContainsKey(ifaceMethodId))
                successors[ifaceMethodId] = new HashSet<string>();
            foreach (var implId in implIds)
                successors[ifaceMethodId].Add(implId);
        }

        var apiCallNodeIds = outboundCalls.Select(c => c.NodeId).ToHashSet();

        var impacts = entryPoints.Select(ep =>
        {
            var reachable = new List<string>();
            var visited   = new HashSet<string>();
            var queue     = new Queue<string>();
            queue.Enqueue(ep.NodeId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current)) continue;

                if (apiCallNodeIds.Contains(current))
                    reachable.Add(current);

                if (successors.TryGetValue(current, out var nexts))
                    foreach (var next in nexts)
                        queue.Enqueue(next);
            }

            return new ApiImpact(ep.NodeId, reachable.Distinct().ToList());
        }).ToList();

        return new SingleApiCrossInfo(apiName, entryPoints, outboundCalls, impacts);
    }

    // ── Virtual dispatch: interface method → concrete implementations ─────────

    private static Dictionary<string, HashSet<string>> BuildVirtualDispatchMap(GraphModel graph)
    {
        // Map each type node to its contained method node-IDs
        var typeToMethods = graph.Edges
            .Where(e => e.Kind == EdgeKind.Contains)
            .GroupBy(e => e.SourceId)
            .Where(g => graph.Nodes.TryGetValue(g.Key, out var n) &&
                        n.Kind is NodeKind.Interface or NodeKind.Class or NodeKind.Struct)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.TargetId)
                       .Where(id => graph.Nodes.TryGetValue(id, out var n) && n.Kind == NodeKind.Method)
                       .ToHashSet());

        // Extract "MethodName(params)" suffix from meta["fullName"] for signature matching
        static string? GetSuffix(GraphNode node)
        {
            if (!node.Meta.TryGetValue("fullName", out var full)) return null;
            var parenIdx = full.IndexOf('(');
            if (parenIdx < 0) return null;
            var dotBefore = full.LastIndexOf('.', parenIdx - 1);
            return dotBefore >= 0 ? full[(dotBefore + 1)..] : full;
        }

        var result = new Dictionary<string, HashSet<string>>();

        foreach (var implEdge in graph.Edges.Where(e => e.Kind == EdgeKind.Implements))
        {
            var classId = implEdge.SourceId;
            var ifaceId = implEdge.TargetId;

            if (!typeToMethods.TryGetValue(ifaceId, out var ifaceMethods)) continue;
            if (!typeToMethods.TryGetValue(classId, out var classMethods)) continue;

            var classBySig = classMethods
                .Select(id => (id, suffix: graph.Nodes.TryGetValue(id, out var n) ? GetSuffix(n) : null))
                .Where(x => x.suffix is not null)
                .ToDictionary(x => x.suffix!, x => x.id);

            foreach (var ifaceMethodId in ifaceMethods)
            {
                if (!graph.Nodes.TryGetValue(ifaceMethodId, out var ifaceMethodNode)) continue;
                var suffix = GetSuffix(ifaceMethodNode);
                if (suffix is null) continue;

                if (classBySig.TryGetValue(suffix, out var classMethodId))
                {
                    if (!result.TryGetValue(ifaceMethodId, out var targets))
                        result[ifaceMethodId] = targets = new HashSet<string>();
                    targets.Add(classMethodId);
                }
            }
        }

        return result;
    }

    // ── Route template matching ───────────────────────────────────────────────

    /// <summary>
    /// Matches a route template against a path, tolerating {param} segments.
    /// Both sides must be pre-normalised: lower-case, no leading slash.
    /// </summary>
    internal static bool RouteTemplatesMatch(string template, string path)
    {
        if (string.Equals(template, path, StringComparison.OrdinalIgnoreCase))
            return true;

        var tParts = template.Split('/');
        var pParts = path.Split('/');
        if (tParts.Length != pParts.Length) return false;

        for (int i = 0; i < tParts.Length; i++)
        {
            var t = tParts[i];
            var p = pParts[i];
            if (string.Equals(t, p, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith('{') && t.EndsWith('}')) continue;
            if (p.StartsWith('{') && p.EndsWith('}')) continue;
            return false;
        }
        return true;
    }
}

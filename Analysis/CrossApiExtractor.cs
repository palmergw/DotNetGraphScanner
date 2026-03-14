using DotNetGraphScanner.Graph;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Shared helpers for canonical cross-API matching logic.
/// </summary>
public static class CrossApiExtractor
{
    // ── Virtual dispatch: interface method → concrete implementations ─────────

    internal static Dictionary<string, HashSet<string>> BuildVirtualDispatchMap(GraphModel graph)
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

using DotNetGraphScanner.Analysis;
using DotNetGraphScanner.Graph;
using Neo4j.Driver;

namespace DotNetGraphScanner.Store;

/// <summary>
/// Persists and retrieves cross-API dependency data in a Neo4j-compatible
/// graph database.
///
/// Graph schema:
///   (:Api            { name, scannedAt })
///   (:EntryPoint     { nodeId, apiName, httpMethod, route, label })
///   (:OutboundCall   { nodeId, ownerApi, targetApi, targetRoute, label })
///
///   (:Api)-[:HAS_ENTRY_POINT]->(:EntryPoint)
///   (:Api)-[:HAS_OUTBOUND_CALL]->(:OutboundCall)
///   (:EntryPoint)-[:CAN_REACH]->(:OutboundCall)      — impact map
///   (:OutboundCall)-[:RESOLVES_TO]->(:EntryPoint)    — cross-API resolved call
/// </summary>
public sealed class Neo4jGraphStore : IAsyncDisposable
{
    private readonly IDriver _driver;

    public Neo4jGraphStore(string uri, string? user = null, string? password = null)
    {
        _driver = user is not null
            ? GraphDatabase.Driver(uri, AuthTokens.Basic(user, password ?? ""))
            : GraphDatabase.Driver(uri, AuthTokens.None);
    }

    // ── Connectivity ──────────────────────────────────────────────────────────

    public async Task VerifyConnectivityAsync(CancellationToken ct = default) =>
        await _driver.VerifyConnectivityAsync();

    /// <summary>
    /// Creates uniqueness constraints (safe to call multiple times; errors are
    /// swallowed to stay compatible with older Neo4j-compatible servers).
    /// </summary>
    public async Task EnsureConstraintsAsync(CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();

        // Memgraph-compatible constraint syntax (no named constraints, no IF NOT EXISTS).
        // Errors (e.g. constraint already exists) are caught below and ignored.
        var statements = new[]
        {
            "CREATE CONSTRAINT ON (a:Api)          ASSERT a.name   IS UNIQUE",
            "CREATE CONSTRAINT ON (e:EntryPoint)   ASSERT e.nodeId IS UNIQUE",
            "CREATE CONSTRAINT ON (o:OutboundCall) ASSERT o.nodeId IS UNIQUE",
            "CREATE CONSTRAINT ON (n:CodeNode)     ASSERT n.id     IS UNIQUE",
        };

        foreach (var stmt in statements)
        {
            try   { await (await session.RunAsync(stmt)).ConsumeAsync(); }
            catch (Exception ex) { Console.WriteLine($"  [neo4j] constraint hint: {ex.Message}"); }
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes a single API's full graph to the database, overwriting any previous
    /// data for that API. Writes all CodeNodes (structural code graph) and typed
    /// relationships, as well as the cross-API metadata (EntryPoints, OutboundCalls,
    /// impact edges). After writing, re-resolves all cross-API connections.
    /// </summary>
    public async Task PushApiAsync(SingleApiCrossInfo info, GraphModel graph, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();

        // 1. Remove existing nodes owned by this API
        await (await session.RunAsync("""
            MATCH (a:Api {name: $apiName})
            OPTIONAL MATCH (a)-[:HAS_ENTRY_POINT]->(e:EntryPoint)
            OPTIONAL MATCH (a)-[:HAS_OUTBOUND_CALL]->(o:OutboundCall)
            DETACH DELETE e, o
            """,
            new Dictionary<string, object?> { ["apiName"] = info.ApiName })).ConsumeAsync();

        // 1b. Remove stale code nodes for this API
        await (await session.RunAsync("""
            MATCH (n:CodeNode {apiName: $apiName})
            DETACH DELETE n
            """,
            new Dictionary<string, object?> { ["apiName"] = info.ApiName })).ConsumeAsync();

        // 2. Upsert the Api node
        await (await session.RunAsync("""
            MERGE (a:Api {name: $apiName})
            SET a.scannedAt = $scannedAt
            """,
            new Dictionary<string, object?> {
                ["apiName"]   = info.ApiName,
                ["scannedAt"] = DateTimeOffset.UtcNow.ToString("o")
            })).ConsumeAsync();

        // 3. Entry points (batched UNWIND)
        if (info.EntryPoints.Count > 0)
        {
            var batch = info.EntryPoints
                .Select(e => new Dictionary<string, object?> {
                    ["nodeId"]     = e.NodeId,
                    ["apiName"]    = e.ApiName,
                    ["httpMethod"] = e.HttpMethod,
                    ["route"]      = e.Route,
                    ["label"]      = e.Label
                })
                .ToList<object>();

            await (await session.RunAsync("""
                UNWIND $batch AS ep
                MERGE (e:EntryPoint {nodeId: ep.nodeId})
                SET e.apiName    = ep.apiName,
                    e.httpMethod = ep.httpMethod,
                    e.route      = ep.route,
                    e.label      = ep.label
                WITH e, ep
                MATCH (a:Api {name: ep.apiName})
                MERGE (a)-[:HAS_ENTRY_POINT]->(e)
                """,
                new Dictionary<string, object?> { ["batch"] = batch })).ConsumeAsync();
        }

        // 4. Outbound calls (batched UNWIND)
        if (info.OutboundCalls.Count > 0)
        {
            var batch = info.OutboundCalls
                .Select(o => new Dictionary<string, object?> {
                    ["nodeId"]      = o.NodeId,
                    ["ownerApi"]    = o.OwnerApi,
                    ["targetApi"]   = o.TargetApi,
                    ["targetRoute"] = o.TargetRoute,
                    ["label"]       = o.Label
                })
                .ToList<object>();

            await (await session.RunAsync("""
                UNWIND $batch AS oc
                MERGE (o:OutboundCall {nodeId: oc.nodeId})
                SET o.ownerApi    = oc.ownerApi,
                    o.targetApi   = oc.targetApi,
                    o.targetRoute = oc.targetRoute,
                    o.label       = oc.label
                WITH o, oc
                MATCH (a:Api {name: oc.ownerApi})
                MERGE (a)-[:HAS_OUTBOUND_CALL]->(o)
                """,
                new Dictionary<string, object?> { ["batch"] = batch })).ConsumeAsync();
        }

        // 5. CAN_REACH impact edges (batched)
        var impactEdges = info.Impacts
            .SelectMany(i => i.ReachableApiCallNodeIds
                .Select(callId => new Dictionary<string, object?> {
                    ["epId"]   = i.EntrypointNodeId,
                    ["callId"] = callId
                }))
            .ToList<object>();

        if (impactEdges.Count > 0)
        {
            await (await session.RunAsync("""
                UNWIND $edges AS e
                MATCH (ep:EntryPoint  {nodeId: e.epId})
                MATCH (oc:OutboundCall {nodeId: e.callId})
                MERGE (ep)-[:CAN_REACH]->(oc)
                """,
                new Dictionary<string, object?> { ["edges"] = impactEdges })).ConsumeAsync();
        }

        // 6. Write code-graph nodes (grouped by NodeKind to set dynamic labels)
        var codeNodeCount = graph.Nodes.Count;
        await WriteCodeNodesAsync(session, graph.Nodes.Values, info.ApiName);

        // 7. Write structural edges (grouped by EdgeKind for relationship type)
        var edgeCount = graph.Edges.Count;
        await WriteStructuralEdgesAsync(session, graph.Edges);

        Console.WriteLine($"  Neo4j ← {info.ApiName} " +
            $"({info.EntryPoints.Count} EPs, {info.OutboundCalls.Count} outbound calls, " +
            $"{impactEdges.Count} impact edges, " +
            $"{codeNodeCount} code nodes, {edgeCount} structural edges)");

        // 8. Re-resolve connections so the view is current
        await ResolveConnectionsAsync(ct);
    }

    // ── Full code-graph write helpers ─────────────────────────────────────────

    private static async Task WriteCodeNodesAsync(
        IAsyncSession session,
        IEnumerable<GraphNode> nodes,
        string apiName)
    {
        // Owned nodes (declared in this API) get a full upsert so re-scans refresh all data.
        // External reference nodes (isExternal=true) are written with ON CREATE only:
        // once a node is claimed by its owning API, later pushes from other APIs must not
        // overwrite its apiName (e.g. ApiCallAttribute belongs to ApiContracts, not FooApi).
        var owned    = nodes.Where(n => !n.Meta.TryGetValue("isExternal", out var e) || e != "true").ToList();
        var external = nodes.Where(n =>  n.Meta.TryGetValue("isExternal", out var e) && e == "true").ToList();

        await WriteNodeBatchAsync(session, owned,    apiName, fullUpsert: true);
        await WriteNodeBatchAsync(session, external, apiName, fullUpsert: false);
    }

    private static async Task WriteNodeBatchAsync(
        IAsyncSession session,
        IEnumerable<GraphNode> nodes,
        string apiName,
        bool fullUpsert)
    {
        foreach (var group in nodes.GroupBy(n => n.Kind))
        {
            var kindName = group.Key.ToString();
            var batch = group.Select(n =>
            {
                var props = new Dictionary<string, object?>
                {
                    ["id"]           = n.Id,
                    ["label"]        = n.Label,
                    ["kind"]         = kindName,
                    ["apiName"]      = apiName,
                    ["isEntryPoint"] = n.IsEntryPoint.ToString()
                };
                foreach (var (k, v) in n.Meta)
                    props[k] = v;
                return (object)props;
            }).ToList<object>();

            if (batch.Count == 0) continue;

            // fullUpsert=true  → full MERGE+SET so re-scans refresh filePath, lineStart, etc.
            //   Uses the exact label combo so the node carries the correct secondary label.
            // fullUpsert=false → ON CREATE only: don't touch nodes already owned by another API.
            //   Merges on CodeNode alone (no secondary label) to avoid a label-mismatch
            //   creating a duplicate node when the owning API already wrote it as :Class, etc.
            var query = fullUpsert
                ? $$"""
                    UNWIND $batch AS n
                    MERGE (c:CodeNode:{{kindName}} {id: n.id})
                    SET c += n
                    """
                : """
                    UNWIND $batch AS n
                    MERGE (c:CodeNode {id: n.id})
                    ON CREATE SET c = n
                    """;

            await (await session.RunAsync(query,
                new Dictionary<string, object?> { ["batch"] = batch })).ConsumeAsync();
        }
    }

    private static async Task WriteStructuralEdgesAsync(
        IAsyncSession session,
        IEnumerable<GraphEdge> edges)
    {
        foreach (var group in edges.GroupBy(e => e.Kind))
        {
            var relType = ToRelType(group.Key);
            var batch = group
                .Select(e => (object)new Dictionary<string, object?> {
                    ["srcId"] = e.SourceId,
                    ["tgtId"] = e.TargetId
                })
                .ToList<object>();

            var query = $$"""
                UNWIND $batch AS e
                MATCH (s:CodeNode {id: e.srcId})
                MATCH (t:CodeNode {id: e.tgtId})
                MERGE (s)-[:{{relType}}]->(t)
                """;
            await (await session.RunAsync(query,
                new Dictionary<string, object?> { ["batch"] = batch })).ConsumeAsync();
        }
    }

    private static string ToRelType(EdgeKind kind) => kind switch
    {
        EdgeKind.ProjectReference => "PROJECT_REFERENCE",
        EdgeKind.PackageReference => "PACKAGE_REFERENCE",
        EdgeKind.EntryPoint       => "ENTRY_POINT",
        EdgeKind.UsesAttribute    => "USES_ATTRIBUTE",
        EdgeKind.ExternalApiCall  => "EXTERNAL_API_CALL",
        _                         => kind.ToString().ToUpperInvariant()
    };

    // ── Impact analysis ───────────────────────────────────────────────────────

    /// <summary>
    /// Given a set of changed file paths and/or function names, returns all HTTP
    /// entry points that can reach those changed nodes via CALLS traversal.
    /// This relies on CodeNode data written by PushApiAsync.
    /// </summary>
    public async Task<List<(string ApiName, string HttpMethod, string Route, string Label)>>
        QueryImpactAsync(
            IReadOnlyList<string> filePaths,
            IReadOnlyList<string> functionNames,
            string? apiFilter,
            CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        var cursor = await session.RunAsync("""
            MATCH (ep:CodeNode {isEntryPoint: 'True'})
            WHERE $apiName IS NULL OR ep.apiName = $apiName
            MATCH (ep)-[:CALLS|ACCESSES|USES_ATTRIBUTE*0..20]->(reached:CodeNode)
            WHERE (size($files) > 0
                   AND reached.filePath IS NOT NULL
                   AND any(fp IN $files WHERE toLower(reached.filePath) CONTAINS toLower(fp)))
               OR (size($fns) > 0
                   AND reached.kind = 'Method'
                   AND any(fn IN $fns WHERE toLower(reached.label) CONTAINS toLower(fn)))
            RETURN DISTINCT ep.apiName       AS apiName,
                            ep.httpMethod    AS httpMethod,
                            ep.routeTemplate AS route,
                            ep.label         AS label
            ORDER BY ep.apiName, ep.routeTemplate
            """,
            new Dictionary<string, object?> {
                ["files"]   = (object)filePaths.ToList(),
                ["fns"]     = (object)functionNames.ToList(),
                ["apiName"] = (object?)apiFilter
            });
        return await cursor.ToListAsync(r => (
            r["apiName"].As<string>(),
            r["httpMethod"].As<string>(),
            r["route"].As<string>(),
            r["label"].As<string>()
        ));
    }

    // ── Diagnostic ────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries the live database and prints a structured diagnostic report:
    /// which APIs are stored, CodeNode counts per kind, entry points, resolved
    /// cross-API connections, and an impact-query smoke-test.
    /// </summary>
    public async Task RunDiagnosticAsync(CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════");
        Console.WriteLine("  Database Diagnostic Report");
        Console.WriteLine("════════════════════════════════════════════");

        // helper: open a new session per query (Memgraph compatibility)
        async Task<List<IRecord>> Q(string cypher, Dictionary<string, object?>? p = null)
        {
            await using var s = _driver.AsyncSession();
            var c = await s.RunAsync(cypher, p ?? new());
            return await c.ToListAsync();
        }

        // 1. APIs in DB
        var apis = await Q("MATCH (a:Api) RETURN a.name AS name, a.scannedAt AS ts ORDER BY a.name");
        Console.WriteLine($"\n  ── APIs ({apis.Count}) ────────────────────────────────");
        if (apis.Count == 0)
            Console.WriteLine("  (none – run scan --push first)");
        foreach (var r in apis)
            Console.WriteLine($"  • {r["name"].As<string>(),-24}  scanned {r["ts"].As<string>()}");

        // 2. CodeNode counts per API
        Console.WriteLine("\n  ── CodeNode counts ──────────────────────────────────");
        var cnCounts = await Q("""
            MATCH (n:CodeNode)
            RETURN n.apiName AS api, n.kind AS kind, count(n) AS cnt
            ORDER BY api, kind
            """);
        if (cnCounts.Count == 0)
            Console.WriteLine("  (no CodeNodes)");
        var byApi = cnCounts.GroupBy(r => r["api"].As<string>());
        foreach (var g in byApi)
        {
            Console.WriteLine($"  {g.Key}:");
            foreach (var r in g)
                Console.WriteLine($"    {r["kind"].As<string>(),-16} {r["cnt"].As<long>(),5}");
        }

        // 3. Solution root nodes (check none are shared across APIs)
        Console.WriteLine("\n  ── Solution root nodes ──────────────────────────────");
        var roots = await Q("MATCH (n:CodeNode {kind:'Solution'}) RETURN n.id AS id, n.label AS lbl, n.apiName AS api ORDER BY api");
        if (roots.Count == 0) Console.WriteLine("  (none)");
        foreach (var r in roots)
            Console.WriteLine($"  id={r["id"].As<string>(),-40}  api={r["api"].As<string>()}  label={r["lbl"].As<string>()}");
        var sharedRoots = roots.GroupBy(r => r["id"].As<string>()).Where(g => g.Count() > 1).ToList();
        if (sharedRoots.Count > 0)
            Console.WriteLine($"  ⚠ {sharedRoots.Count} root ID(s) shared across multiple scans — rescan all APIs to fix.");

        // 4. Entry points
        Console.WriteLine("\n  ── Entry points ─────────────────────────────────────");
        var eps = await Q("""
            MATCH (n:CodeNode {isEntryPoint:'True'})
            RETURN n.apiName AS api, n.httpMethod AS m, n.routeTemplate AS rt
            ORDER BY api, rt
            """);
        if (eps.Count == 0) Console.WriteLine("  (none)");
        foreach (var r in eps)
            Console.WriteLine($"  [{r["api"].As<string>(),-18}] {r["m"].As<string>(),-6} {r["rt"].As<string>()}");

        // 5. Cross-API connections resolved
        var connCount = await Q("MATCH ()-[r:RESOLVES_TO]->() RETURN count(r) AS cnt");
        Console.WriteLine($"\n  ── Cross-API resolved connections: {connCount[0]["cnt"].As<long>()} ──────────");

        // 6. ApiCallAttribute node check
        Console.WriteLine("\n  ── ApiCallAttribute node ────────────────────────────");
        var attrNodes = await Q("MATCH (n:CodeNode {label:'ApiCallAttribute'}) RETURN n.apiName AS api, n.kind AS kind, n.filePath AS fp");
        if (attrNodes.Count == 0)
            Console.WriteLine("  ✗ NOT FOUND – scan ApiContracts with --push");
        foreach (var r in attrNodes)
            Console.WriteLine($"  ✓ apiName={r["api"].As<string>()}  kind={r["kind"].As<string>()}  filePath={r["fp"].As<string>()}");

        // 7. Duplicate CodeNode id check
        Console.WriteLine("\n  ── Duplicate CodeNode ids (should be empty) ──────────");
        var dups = await Q("""
            MATCH (n:CodeNode)
            WITH n.id AS id, count(n) AS cnt
            WHERE cnt > 1
            RETURN id, cnt ORDER BY cnt DESC LIMIT 10
            """);
        if (dups.Count == 0)
            Console.WriteLine("  (none – good)");
        foreach (var r in dups)
            Console.WriteLine($"  ⚠ id={r["id"].As<string>()[..Math.Min(60, r["id"].As<string>().Length)]}  cnt={r["cnt"].As<long>()}");

        // 8. Property nodes from Models.cs - do they have filePath?
        var props = await Q("""
            MATCH (n:CodeNode {kind:'Property'})
            WHERE n.filePath IS NOT NULL
              AND toLower(n.filePath) CONTAINS 'models'
            RETURN n.label AS lbl, n.filePath AS fp, n.apiName AS api
            ORDER BY api, lbl LIMIT 20
            """);
        Console.WriteLine($"\n  ── Property nodes in Models file ({props.Count}) ────────────");
        if (props.Count == 0)
            Console.WriteLine("  ✗ None found – rescan with updated code");
        foreach (var r in props)
        {
            var fp = r["fp"].As<string>();
            // Show last 3 path segments with forward slashes for readability
            var fpShort = fp.Replace('\\', '/').Split('/').TakeLast(3).Aggregate((a,b) => a+"/"+b);
            Console.WriteLine($"  [{r["api"].As<string>(),-14}] {r["lbl"].As<string>(),-18}  {fpShort}");
        }
        // Show one raw full path so we know exactly what to search for in the UI
        if (props.Count > 0)
        {
            var rawFp = props[0]["fp"].As<string>();
            Console.WriteLine($"\n  ↳ Raw filePath example (search for any fragment of this):");
            Console.WriteLine($"    {rawFp}");
        }

        // 9. Direct path: entry point → ACCESSES → Models.cs property (1 hop)
        var direct = await Q("""
            MATCH (ep:CodeNode {isEntryPoint:'True'})-[:ACCESSES]->(n:CodeNode)
            WHERE n.filePath IS NOT NULL
              AND toLower(n.filePath) CONTAINS 'models'
            RETURN DISTINCT ep.apiName AS api, ep.routeTemplate AS rt,
                            n.label AS node, n.filePath AS fp
            ORDER BY api, rt LIMIT 20
            """);
        Console.WriteLine($"\n  ── EP →[ACCESSES]→ Models node (1 hop, {direct.Count}) ─────");
        if (direct.Count == 0)
            Console.WriteLine("  ✗ No direct ACCESSES edges to Models nodes from entry points");
        foreach (var r in direct)
        {
            var fp = r["fp"].As<string>().Replace('\\', '/');
            Console.WriteLine($"  [{r["api"].As<string>(),-14}] {r["rt"].As<string>(),-28} → {r["node"].As<string>()}");
        }

        // 10. Paths via intermediate methods (2-3 hops)
        var indirect = await Q("""
            MATCH (ep:CodeNode {isEntryPoint:'True'})-[:CALLS|ACCESSES*2..4]->(n:CodeNode)
            WHERE n.filePath IS NOT NULL
              AND toLower(n.filePath) CONTAINS 'models'
            RETURN DISTINCT ep.apiName AS api, ep.routeTemplate AS rt,
                            n.label AS node
            ORDER BY api, rt LIMIT 20
            """);
        Console.WriteLine($"\n  ── EP →[CALLS|ACCESSES*2..4]→ Models node ({indirect.Count}) ──");
        if (indirect.Count == 0)
            Console.WriteLine("  ✗ No indirect paths to Models nodes");
        foreach (var r in indirect)
            Console.WriteLine($"  [{r["api"].As<string>(),-14}] {r["rt"].As<string>(),-28} → {r["node"].As<string>()}");

        // 11. Smoke-test impact query for Models.cs (FooApi)
        Console.WriteLine("\n  ── Impact smoke-test: 'models.cs' ───────────────────");
        try
        {
            var impHits = await QueryImpactAsync(["models.cs"], [], null, ct);
            if (impHits.Count == 0)
                Console.WriteLine("  ✗ No results for 'models.cs' — check edge coverage or rescan");
            else
                foreach (var (api, method, route, label) in impHits)
                    Console.WriteLine($"  ✓ [{api}] {method} {route}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ Query failed: {ex.Message}");
        }

        Console.WriteLine("\n════════════════════════════════════════════════════\n");
    }

    // ── Connection resolution ─────────────────────────────────────────────────

    /// <summary>
    /// (Re)builds all RESOLVES_TO edges between OutboundCall nodes and their
    /// matching EntryPoint nodes on the target API.  Runs after every push so
    /// the live view always reflects the current state of all APIs in the DB.
    /// </summary>
    public async Task ResolveConnectionsAsync(CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();

        // Drop all existing RESOLVES_TO before rebuilding
        await (await session.RunAsync("MATCH ()-[r:RESOLVES_TO]->() DELETE r")).ConsumeAsync();

        // Fetch all entry points
        var epCursor = await session.RunAsync("""
            MATCH (e:EntryPoint)
            RETURN e.nodeId AS nodeId, e.apiName AS apiName,
                   e.httpMethod AS httpMethod, e.route AS route
            """);
        var eps = await epCursor.ToListAsync(r => (
            NodeId:     r["nodeId"].As<string>(),
            ApiName:    r["apiName"].As<string>(),
            HttpMethod: r["httpMethod"].As<string>(),
            Route:      r["route"].As<string>()));

        // Fetch all outbound calls
        var outCursor = await session.RunAsync("""
            MATCH (o:OutboundCall)
            RETURN o.nodeId AS nodeId, o.targetApi AS targetApi, o.targetRoute AS targetRoute
            """);
        var outs = await outCursor.ToListAsync(r => (
            NodeId:      r["nodeId"].As<string>(),
            TargetApi:   r["targetApi"].As<string>(),
            TargetRoute: r["targetRoute"].As<string>()));

        // Build lookup: apiName → list of entry points
        var epIndex = eps
            .GroupBy(e => e.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // C#-side route matching (same logic as CrossApiExtractor)
        var resolvedEdges = new List<object>();
        foreach (var o in outs)
        {
            var parts = o.TargetRoute.Split(' ', 2, StringSplitOptions.TrimEntries);
            var verb  = parts.Length > 1 ? parts[0].ToUpperInvariant() : "HTTP";
            var path  = (parts.Length > 1 ? parts[1] : parts[0]).Trim('/').ToLowerInvariant();

            if (!epIndex.TryGetValue(o.TargetApi, out var candidates)) continue;

            var match = candidates.FirstOrDefault(ep =>
                string.Equals(ep.HttpMethod.ToUpperInvariant(), verb, StringComparison.Ordinal) &&
                CrossApiExtractor.RouteTemplatesMatch(
                    ep.Route.Trim('/').ToLowerInvariant(), path));

            if (match != default)
                resolvedEdges.Add(new Dictionary<string, object?> {
                    ["outId"] = o.NodeId,
                    ["epId"]  = match.NodeId
                });
        }

        if (resolvedEdges.Count > 0)
        {
            await (await session.RunAsync("""
                UNWIND $edges AS e
                MATCH (o:OutboundCall {nodeId: e.outId})
                MATCH (ep:EntryPoint   {nodeId: e.epId})
                MERGE (o)-[:RESOLVES_TO]->(ep)
                """,
                new Dictionary<string, object?> { ["edges"] = resolvedEdges })).ConsumeAsync();
        }

        Console.WriteLine($"  Neo4j   resolved {resolvedEdges.Count} cross-API connections");
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>Removes all cross-API nodes and relationships from the database.</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        await (await session.RunAsync("""
            MATCH (n)
            WHERE n:Api OR n:EntryPoint OR n:OutboundCall
            DETACH DELETE n
            """)).ConsumeAsync();
    }

    public ValueTask DisposeAsync() => _driver.DisposeAsync();
}

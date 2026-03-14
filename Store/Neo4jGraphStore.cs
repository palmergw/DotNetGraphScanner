using DotNetGraphScanner.Analysis;
using DotNetGraphScanner.Graph;
using Neo4j.Driver;

namespace DotNetGraphScanner.Store;

/// <summary>
/// Persists and retrieves canonical code-graph dependency data in a Neo4j-compatible
/// graph database.
///
/// Graph schema:
///   (:Api      { name, scannedAt })
///   (:CodeNode { id, kind, apiName, ... })
///   (:CodeNode)-[:RESOLVES_TO]->(:CodeNode)
/// </summary>
public sealed class Neo4jGraphStore : IAsyncDisposable
{
    private readonly IDriver _driver;

    private readonly record struct ResolutionEntryPoint(
        string NodeId,
        string ApiName,
        string HttpMethod,
        string Route);

    private readonly record struct ResolutionOutboundCall(
        string NodeId,
        string SourceApi,
        string TargetApi,
        string TargetRoute);

    private readonly record struct ResolvedConnectionEdge(
        string OutboundNodeId,
        string EntryPointNodeId,
        string SourceApi,
        string TargetApi);

    private readonly record struct ReachabilityNode(
        string NodeId,
        string Kind,
        string? FullName);

    private readonly record struct ReachabilityEdge(
        string SourceId,
        string Rel,
        string TargetId);

    private readonly record struct CanonicalImpactResult(
        string EntryPointNodeId,
        string ApiName,
        IReadOnlyList<string> CallNodeIds);

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
    /// canonical CodeNode data for that API. After writing structural data, the
    /// store re-resolves canonical cross-API connections.
    /// </summary>
    public async Task PushApiAsync(string apiName, GraphModel graph, CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        var scannedAt = DateTimeOffset.UtcNow.ToString("o");

        // 1b. Remove stale code nodes for this API
        await (await session.RunAsync("""
            MATCH (n:CodeNode {apiName: $apiName})
            DETACH DELETE n
            """,
            new Dictionary<string, object?> { ["apiName"] = apiName })).ConsumeAsync();

        // 2. Upsert the Api node
        await (await session.RunAsync("""
            MERGE (a:Api {name: $apiName})
            SET a.scannedAt = $scannedAt
            """,
            new Dictionary<string, object?> {
                ["apiName"]   = apiName,
                ["scannedAt"] = scannedAt
            })).ConsumeAsync();

        // 3. Write code-graph nodes (grouped by NodeKind to set dynamic labels)
        var codeNodeCount = graph.Nodes.Count;
        await WriteCodeNodesAsync(session, graph.Nodes.Values, apiName, scannedAt);

        // 4. Write structural edges (grouped by EdgeKind for relationship type)
        var edgeCount = graph.Edges.Count;
        await WriteStructuralEdgesAsync(session, graph.Edges);

        var entryPointCount = graph.Nodes.Values.Count(node =>
            node.Kind == NodeKind.Method &&
            node.IsEntryPoint &&
            node.Meta.ContainsKey("httpMethod") &&
            node.Meta.ContainsKey("routeTemplate"));
        var outboundCallCount = graph.Nodes.Values.Count(node =>
            node.Kind == NodeKind.Method &&
            node.Meta.TryGetValue("isApiCall", out var isApiCall) &&
            string.Equals(isApiCall, "true", StringComparison.OrdinalIgnoreCase) &&
            node.Meta.ContainsKey("targetApi") &&
            node.Meta.ContainsKey("targetRoute"));

        Console.WriteLine($"  Neo4j ← {apiName} " +
            $"({entryPointCount} EPs, {outboundCallCount} outbound calls, " +
            $"{codeNodeCount} code nodes, {edgeCount} structural edges)");

        // 5. Re-resolve connections so the view is current
        await ResolveConnectionsAsync(ct);
    }

    // ── Full code-graph write helpers ─────────────────────────────────────────

    private static async Task WriteCodeNodesAsync(
        IAsyncSession session,
        IEnumerable<GraphNode> nodes,
        string apiName,
        string scannedAt)
    {
        // Owned nodes (declared in this API) get a full upsert so re-scans refresh all data.
        // External reference nodes (isExternal=true) are written with ON CREATE only:
        // once a node is claimed by its owning API, later pushes from other APIs must not
        // overwrite its apiName (e.g. ApiCallAttribute belongs to ApiContracts, not FooApi).
        var owned    = nodes.Where(n => !n.Meta.TryGetValue("isExternal", out var e) || e != "true").ToList();
        var external = nodes.Where(n =>  n.Meta.TryGetValue("isExternal", out var e) && e == "true").ToList();

        await WriteNodeBatchAsync(session, owned,    apiName, scannedAt, fullUpsert: true);
        await WriteNodeBatchAsync(session, external, apiName, scannedAt, fullUpsert: false);
    }

    private static async Task WriteNodeBatchAsync(
        IAsyncSession session,
        IEnumerable<GraphNode> nodes,
        string apiName,
        string scannedAt,
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
                    ["isEntryPoint"] = n.IsEntryPoint.ToString(),
                    ["scannedAt"]    = scannedAt
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

        // 5. API dependency card counts
        Console.WriteLine("\n  ── API dependency card counts ─────────────────────");
        var canonicalEpCounts = await Q("""
            MATCH (e:CodeNode)
            WHERE e.kind = 'Method'
              AND e.isEntryPoint = 'True'
              AND e.httpMethod IS NOT NULL
              AND e.routeTemplate IS NOT NULL
            RETURN e.apiName AS api, count(e) AS cnt
            ORDER BY api
            """);
        var canonicalOutCounts = await Q("""
            MATCH (o:CodeNode)
            WHERE o.kind = 'Method'
              AND o.isApiCall = 'true'
              AND o.targetApi IS NOT NULL
              AND o.targetRoute IS NOT NULL
            RETURN o.apiName AS api, count(o) AS cnt
            ORDER BY api
            """);

        static Dictionary<string, long> ToCountMap(IEnumerable<IRecord> rows) => rows
            .GroupBy(r => r["api"].As<string>(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()["cnt"].As<long>(), StringComparer.OrdinalIgnoreCase);

        var canonicalEpMap = ToCountMap(canonicalEpCounts);
        var canonicalOutMap = ToCountMap(canonicalOutCounts);
        var countApis = canonicalEpMap.Keys
            .Union(canonicalOutMap.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (countApis.Count == 0)
            Console.WriteLine("  (no API dependency cards found)");
        foreach (var api in countApis)
        {
            var canonEp = canonicalEpMap.GetValueOrDefault(api, 0);
            var canonOut = canonicalOutMap.GetValueOrDefault(api, 0);
            Console.WriteLine($"  • [{api,-18}] entry={canonEp,-3} outbound={canonOut,-3}");
        }

        // 6. Canonical resolved connections
        Console.WriteLine("\n  ── Canonical resolved connections ─────────────────");
        var canonicalResolutionEntryRecs = await Q("""
            MATCH (e:CodeNode)
            WHERE e.kind = 'Method'
              AND e.isEntryPoint = 'True'
              AND e.httpMethod IS NOT NULL
              AND e.routeTemplate IS NOT NULL
            RETURN e.id AS nodeId, e.apiName AS apiName,
                   e.httpMethod AS httpMethod, e.routeTemplate AS route
            ORDER BY apiName, route
            """);
        var canonicalResolutionOutRecs = await Q("""
            MATCH (o:CodeNode)
            WHERE o.kind = 'Method'
              AND o.isApiCall = 'true'
              AND o.targetApi IS NOT NULL
              AND o.targetRoute IS NOT NULL
            RETURN o.id AS nodeId, o.apiName AS sourceApi,
                   o.targetApi AS targetApi, o.targetRoute AS targetRoute
            ORDER BY sourceApi, targetApi, targetRoute
            """);

        var canonicalResolved = ResolveResolvedConnections(
            canonicalResolutionEntryRecs.Select(r => new ResolutionEntryPoint(
                r["nodeId"].As<string>(),
                r["apiName"].As<string>(),
                r["httpMethod"].As<string>(),
                r["route"].As<string>())).ToList(),
            canonicalResolutionOutRecs.Select(r => new ResolutionOutboundCall(
                r["nodeId"].As<string>(),
                r["sourceApi"].As<string>(),
                r["targetApi"].As<string>(),
                r["targetRoute"].As<string>())).ToList());

        var storedCanonicalConnCount = await Q("""
            MATCH (o:CodeNode)-[r:RESOLVES_TO]->(e:CodeNode)
            WHERE o.kind = 'Method'
              AND e.kind = 'Method'
            RETURN count(r) AS cnt
            """);

        static Dictionary<string, long> ToApiMatchCountMap(IEnumerable<ResolvedConnectionEdge> edges) =>
            edges.GroupBy(e => e.SourceApi ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.OrdinalIgnoreCase);

        var storedCanonicalCount = storedCanonicalConnCount[0]["cnt"].As<long>();
        var storedMatchesComputed = storedCanonicalCount == canonicalResolved.Count;
        Console.WriteLine($"  {(storedMatchesComputed ? '✓' : '✗')} computed={canonicalResolved.Count} stored={storedCanonicalCount}");

        var canonicalResolvedByApi = ToApiMatchCountMap(canonicalResolved);
        var resolutionApis = canonicalResolvedByApi.Keys
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var api in resolutionApis)
        {
            var canonicalCount = canonicalResolvedByApi.GetValueOrDefault(api, 0);
            Console.WriteLine($"  • [{api,-18}] canonical-computed={canonicalCount,-3}");
        }

        // 7. Canonical reachability
        Console.WriteLine("\n  ── Canonical reachability ─────────────────────────");
        var canonicalImpactEntryRecs = await Q("""
            MATCH (e:CodeNode)
            WHERE e.kind = 'Method'
              AND e.isEntryPoint = 'True'
              AND e.httpMethod IS NOT NULL
              AND e.routeTemplate IS NOT NULL
            RETURN e.id AS nodeId, e.apiName AS apiName,
                   e.httpMethod AS httpMethod, e.routeTemplate AS route
            ORDER BY apiName, route
            """);
        var canonicalImpactOutRecs = await Q("""
            MATCH (o:CodeNode)
            WHERE o.kind = 'Method'
              AND o.isApiCall = 'true'
              AND o.targetApi IS NOT NULL
              AND o.targetRoute IS NOT NULL
            RETURN o.id AS nodeId, o.apiName AS sourceApi,
                   o.targetApi AS targetApi, o.targetRoute AS targetRoute
            ORDER BY sourceApi, targetApi, targetRoute
            """);
        var canonicalImpactNodeRecs = await Q("""
            MATCH (n:CodeNode)
            WHERE n.kind IN ['Method','Class','Interface','Struct']
            RETURN n.id AS id, n.kind AS kind, n.fullName AS fullName
            """);
        var canonicalImpactEdgeRecs = await Q("""
            MATCH (s:CodeNode)-[r]->(t:CodeNode)
            WHERE type(r) IN ['CALLS','CONTAINS','IMPLEMENTS']
            RETURN s.id AS src, type(r) AS rel, t.id AS tgt
            """);

        var canonicalImpacts = BuildCanonicalImpacts(
            canonicalImpactEntryRecs.Select(r => new ResolutionEntryPoint(
                r["nodeId"].As<string>(),
                r["apiName"].As<string>(),
                r["httpMethod"].As<string>(),
                r["route"].As<string>())).ToList(),
            canonicalImpactOutRecs.Select(r => new ResolutionOutboundCall(
                r["nodeId"].As<string>(),
                r["sourceApi"].As<string>(),
                r["targetApi"].As<string>(),
                r["targetRoute"].As<string>())).ToList(),
            canonicalImpactNodeRecs.Select(r => new ReachabilityNode(
                r["id"].As<string>(),
                r["kind"].As<string>(),
                r["fullName"].As<string?>())).ToList(),
            canonicalImpactEdgeRecs.Select(r => new ReachabilityEdge(
                r["src"].As<string>(),
                r["rel"].As<string>(),
                r["tgt"].As<string>())).ToList());

        static Dictionary<string, long> ToApiImpactCountMap(IEnumerable<CanonicalImpactResult> impacts) =>
            impacts.GroupBy(impact => impact.ApiName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (long)group.Sum(impact => impact.CallNodeIds.Count),
                    StringComparer.OrdinalIgnoreCase);

        var canonicalImpactCount = canonicalImpacts.Sum(impact => impact.CallNodeIds.Count);
        Console.WriteLine($"  • canonical BFS={canonicalImpactCount}");

        var canonicalImpactByApi = ToApiImpactCountMap(canonicalImpacts);
        var impactApis = canonicalImpactByApi.Keys
            .OrderBy(api => api, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var api in impactApis)
        {
            var canonicalCount = canonicalImpactByApi.GetValueOrDefault(api, 0);
            Console.WriteLine($"  • [{api,-18}] canonical BFS={canonicalCount,-3}");
        }

        // 8. ApiCallAttribute node check
        Console.WriteLine("\n  ── ApiCallAttribute node ────────────────────────────");
        var attrNodes = await Q("MATCH (n:CodeNode {label:'ApiCallAttribute'}) RETURN n.apiName AS api, n.kind AS kind, n.filePath AS fp");
        if (attrNodes.Count == 0)
            Console.WriteLine("  ✗ NOT FOUND – scan ApiContracts with --push");
        foreach (var r in attrNodes)
            Console.WriteLine($"  ✓ apiName={r["api"].As<string>()}  kind={r["kind"].As<string>()}  filePath={r["fp"].As<string>()}");

        // 9. Duplicate CodeNode id check
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

        // 10. Property nodes from Models.cs - do they have filePath?
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

                // 11. Direct path: entry point → ACCESSES → Models.cs property (1 hop)
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

                // 12. Paths via intermediate methods (2-3 hops)
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

        // 13. Smoke-test impact query for Models.cs (FooApi)
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
    /// (Re)builds all RESOLVES_TO edges between outbound-call CodeNode methods
    /// and matching entry-point CodeNode methods on the target API. Runs after
    /// every push so the live view reflects the current state of all APIs.
    /// </summary>
    public async Task ResolveConnectionsAsync(CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();

        // Drop all existing RESOLVES_TO before rebuilding
        await (await session.RunAsync("MATCH ()-[r:RESOLVES_TO]->() DELETE r")).ConsumeAsync();

        // Fetch all entry points
        var epCursor = await session.RunAsync("""
            MATCH (e:CodeNode)
            WHERE e.kind = 'Method'
              AND e.isEntryPoint = 'True'
              AND e.httpMethod IS NOT NULL
              AND e.routeTemplate IS NOT NULL
            RETURN e.id AS nodeId, e.apiName AS apiName,
                   e.httpMethod AS httpMethod, e.routeTemplate AS route
            """);
        var eps = await epCursor.ToListAsync(r => new ResolutionEntryPoint(
            r["nodeId"].As<string>(),
            r["apiName"].As<string>(),
            r["httpMethod"].As<string>(),
            r["route"].As<string>()));

        // Fetch all outbound calls
        var outCursor = await session.RunAsync("""
            MATCH (o:CodeNode)
            WHERE o.kind = 'Method'
              AND o.isApiCall = 'true'
              AND o.targetApi IS NOT NULL
              AND o.targetRoute IS NOT NULL
            RETURN o.id AS nodeId, o.apiName AS sourceApi,
                   o.targetApi AS targetApi, o.targetRoute AS targetRoute
            """);
        var outs = await outCursor.ToListAsync(r => new ResolutionOutboundCall(
            r["nodeId"].As<string>(),
            r["sourceApi"].As<string>(),
            r["targetApi"].As<string>(),
            r["targetRoute"].As<string>()));

        var resolvedEdges = ResolveResolvedConnections(eps, outs)
            .Select(edge => new Dictionary<string, object?> {
                ["outId"] = edge.OutboundNodeId,
                ["epId"] = edge.EntryPointNodeId
            })
            .ToList<object>();

        if (resolvedEdges.Count > 0)
        {
            await (await session.RunAsync("""
                UNWIND $edges AS e
                MATCH (o:CodeNode {id: e.outId})
                MATCH (ep:CodeNode {id: e.epId})
                MERGE (o)-[:RESOLVES_TO]->(ep)
                """,
                new Dictionary<string, object?> { ["edges"] = resolvedEdges })).ConsumeAsync();
        }

        Console.WriteLine($"  Neo4j   resolved {resolvedEdges.Count} canonical cross-API connections");
    }

    private static List<ResolvedConnectionEdge> ResolveResolvedConnections(
        IReadOnlyList<ResolutionEntryPoint> entryPoints,
        IReadOnlyList<ResolutionOutboundCall> outboundCalls)
    {
        var epIndex = entryPoints
            .GroupBy(e => e.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var resolvedEdges = new List<ResolvedConnectionEdge>();
        foreach (var outboundCall in outboundCalls)
        {
            var parts = outboundCall.TargetRoute.Split(' ', 2, StringSplitOptions.TrimEntries);
            var verb = parts.Length > 1 ? parts[0].ToUpperInvariant() : "HTTP";
            var path = (parts.Length > 1 ? parts[1] : parts[0]).Trim('/').ToLowerInvariant();

            if (!epIndex.TryGetValue(outboundCall.TargetApi, out var candidates))
                continue;

            var match = candidates.FirstOrDefault(entryPoint =>
                string.Equals(entryPoint.HttpMethod.ToUpperInvariant(), verb, StringComparison.Ordinal) &&
                CrossApiExtractor.RouteTemplatesMatch(
                    entryPoint.Route.Trim('/').ToLowerInvariant(),
                    path));

            if (match == default)
                continue;

            resolvedEdges.Add(new ResolvedConnectionEdge(
                outboundCall.NodeId,
                match.NodeId,
                outboundCall.SourceApi,
                match.ApiName));
        }

        return resolvedEdges;
    }

    private static List<CanonicalImpactResult> BuildCanonicalImpacts(
        IReadOnlyList<ResolutionEntryPoint> entryPoints,
        IReadOnlyList<ResolutionOutboundCall> outboundCalls,
        IReadOnlyList<ReachabilityNode> graphNodes,
        IReadOnlyList<ReachabilityEdge> graphEdges)
    {
        var nodeById = graphNodes.ToDictionary(node => node.NodeId, StringComparer.OrdinalIgnoreCase);
        var successors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var typeToMethods = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var implEdges = new List<ReachabilityEdge>();

        foreach (var edge in graphEdges)
        {
            if (string.Equals(edge.Rel, "CALLS", StringComparison.OrdinalIgnoreCase))
            {
                if (!successors.TryGetValue(edge.SourceId, out var targets))
                    successors[edge.SourceId] = targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targets.Add(edge.TargetId);
                continue;
            }

            if (string.Equals(edge.Rel, "CONTAINS", StringComparison.OrdinalIgnoreCase))
            {
                if (!nodeById.TryGetValue(edge.SourceId, out var srcNode) || !nodeById.TryGetValue(edge.TargetId, out var targetNode))
                    continue;
                if (targetNode.Kind != nameof(NodeKind.Method))
                    continue;
                if (srcNode.Kind is not (nameof(NodeKind.Interface) or nameof(NodeKind.Class) or nameof(NodeKind.Struct)))
                    continue;

                if (!typeToMethods.TryGetValue(edge.SourceId, out var methods))
                    typeToMethods[edge.SourceId] = methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                methods.Add(edge.TargetId);
                continue;
            }

            if (string.Equals(edge.Rel, "IMPLEMENTS", StringComparison.OrdinalIgnoreCase))
                implEdges.Add(edge);
        }

        foreach (var edge in implEdges)
        {
            if (!typeToMethods.TryGetValue(edge.TargetId, out var interfaceMethods) || !typeToMethods.TryGetValue(edge.SourceId, out var classMethods))
                continue;

            var classBySignature = classMethods
                .Select(methodId => (MethodId: methodId, Suffix: nodeById.TryGetValue(methodId, out var methodNode) ? GetMethodSuffix(methodNode.FullName) : null))
                .Where(x => x.Suffix is not null)
                .ToDictionary(x => x.Suffix!, x => x.MethodId, StringComparer.OrdinalIgnoreCase);

            foreach (var interfaceMethodId in interfaceMethods)
            {
                if (!nodeById.TryGetValue(interfaceMethodId, out var interfaceMethodNode))
                    continue;
                var suffix = GetMethodSuffix(interfaceMethodNode.FullName);
                if (suffix is null || !classBySignature.TryGetValue(suffix, out var classMethodId))
                    continue;

                if (!successors.TryGetValue(interfaceMethodId, out var implTargets))
                    successors[interfaceMethodId] = implTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                implTargets.Add(classMethodId);
            }
        }

        var apiCallIds = outboundCalls.Select(call => call.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var impacts = new List<CanonicalImpactResult>();

        foreach (var entryPoint in entryPoints)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(entryPoint.NodeId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current))
                    continue;

                if (apiCallIds.Contains(current))
                    reachable.Add(current);

                if (!successors.TryGetValue(current, out var nextIds))
                    continue;

                foreach (var nextId in nextIds)
                    if (!visited.Contains(nextId))
                        queue.Enqueue(nextId);
            }

            if (reachable.Count == 0)
                continue;

            impacts.Add(new CanonicalImpactResult(
                entryPoint.NodeId,
                entryPoint.ApiName,
                reachable.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return impacts;
    }

    private static string? GetMethodSuffix(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return null;

        var parenIndex = fullName.IndexOf('(');
        if (parenIndex < 0)
            return null;

        var dotBefore = fullName.LastIndexOf('.', parenIndex - 1);
        return dotBefore >= 0 ? fullName[(dotBefore + 1)..] : fullName;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>Removes all stored Api and CodeNode data from the database.</summary>
    public async Task ClearStoreAsync(CancellationToken ct = default)
    {
        await using var session = _driver.AsyncSession();
        await (await session.RunAsync("""
            MATCH (n)
            WHERE n:Api OR n:CodeNode
            DETACH DELETE n
            """)).ConsumeAsync();
    }

    public ValueTask DisposeAsync() => _driver.DisposeAsync();
}

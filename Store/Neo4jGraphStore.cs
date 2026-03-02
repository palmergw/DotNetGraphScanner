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
        };

        foreach (var stmt in statements)
        {
            try   { await (await session.RunAsync(stmt)).ConsumeAsync(); }
            catch (Exception ex) { Console.WriteLine($"  [neo4j] constraint hint: {ex.Message}"); }
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes a single API's cross-API metadata to the database, overwriting
    /// any previous data for that API. After writing, re-resolves all
    /// cross-API connections so the live view is immediately up to date.
    /// </summary>
    public async Task PushApiAsync(SingleApiCrossInfo info, CancellationToken ct = default)
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

        Console.WriteLine($"  Neo4j ← {info.ApiName} " +
            $"({info.EntryPoints.Count} EPs, {info.OutboundCalls.Count} outbound calls, " +
            $"{impactEdges.Count} impact edges)");

        // 6. Re-resolve connections so the view is current
        await ResolveConnectionsAsync(ct);
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

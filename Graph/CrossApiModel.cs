namespace DotNetGraphScanner.Graph;

/// <summary>
/// Describes a single public HTTP endpoint on an API.
/// </summary>
public sealed record EntrypointInfo(
    string NodeId,
    string ApiName,
    string HttpMethod,
    string Route,
    string Label);

/// <summary>
/// Describes an outbound call from one API to an endpoint on another API.
/// This corresponds to a method decorated with [ApiCall("TargetApi", "VERB /route")].
/// </summary>
public sealed record ApiCallInfo(
    string NodeId,
    string OwnerApi,
    string TargetApi,
    string TargetRoute,
    string Label);

/// <summary>
/// Maps a single entry point to every outbound API call that can be reached
/// by following the internal call graph from that entry point.
/// </summary>
public sealed record ApiImpact(
    string EntrypointNodeId,
    IReadOnlyList<string> ReachableApiCallNodeIds);

/// <summary>
/// Cross-API connection: an outbound call node on one API matched to the
/// corresponding entry point node on the target API.
/// </summary>
public sealed record CrossApiConnection(
    string OutboundCallNodeId,
    string OwnerApi,
    string TargetApi,
    string TargetRoute,
    string? MatchedEntrypointNodeId);   // null when no entry point could be matched

/// <summary>
/// Per-API cross-API metadata extracted from a single scan.
/// Pushed to the persistence store and later aggregated for the live view.
/// </summary>
public sealed record SingleApiCrossInfo(
    string ApiName,
    IReadOnlyList<EntrypointInfo> EntryPoints,
    IReadOnlyList<ApiCallInfo> OutboundCalls,
    IReadOnlyList<ApiImpact> Impacts);

/// <summary>
/// Top-level result from a multi-API scan (retained for the static cross-scan path).
/// </summary>
public sealed class CrossApiResult
{
    public GraphModel Graph { get; init; } = new();
    public IReadOnlyList<string> ApiNames { get; init; } = [];
    public IReadOnlyList<EntrypointInfo> EntryPoints { get; init; } = [];
    public IReadOnlyList<ApiCallInfo> OutboundCalls { get; init; } = [];
    public IReadOnlyList<ApiImpact> Impacts { get; init; } = [];
    public IReadOnlyList<CrossApiConnection> Connections { get; init; } = [];
}

namespace DotNetGraphScanner.Graph;

public sealed class GraphEdge
{
    public string Id { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public EdgeKind Kind { get; set; }
    public string Label => Kind.ToString();

    /// <summary>Additional metadata (e.g. call site line number).</summary>
    public Dictionary<string, string> Meta { get; set; } = new();
}

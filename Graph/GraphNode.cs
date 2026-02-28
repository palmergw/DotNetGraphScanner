using System.Text.Json.Serialization;

namespace DotNetGraphScanner.Graph;

public sealed class GraphNode
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public NodeKind Kind { get; set; }
    public bool IsEntryPoint { get; set; }

    /// <summary>Additional metadata stored as key/value pairs.</summary>
    public Dictionary<string, string> Meta { get; set; } = new();

    [JsonIgnore]
    public string KindName => Kind.ToString();
}

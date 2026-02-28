using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetGraphScanner.Graph;

namespace DotNetGraphScanner.Export;

/// <summary>
/// Exports the graph as a JSON file containing { nodes: [...], edges: [...] }.
/// This format can be consumed by Neo4j import tools, custom scripts, or any
/// downstream graph processing.
/// </summary>
public sealed class JsonExporter : IGraphExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Task ExportAsync(GraphModel graph, string outputPath, CancellationToken ct = default)
    {
        var payload = new
        {
            nodes = graph.Nodes.Values.Select(n => new
            {
                id = n.Id,
                label = n.Label,
                kind = n.Kind.ToString(),
                isEntryPoint = n.IsEntryPoint,
                meta = n.Meta
            }),
            edges = graph.Edges.Select(e => new
            {
                id = e.Id,
                source = e.SourceId,
                target = e.TargetId,
                kind = e.Kind.ToString(),
                label = e.Label,
                meta = e.Meta
            }),
            stats = new
            {
                nodeCount = graph.Nodes.Count,
                edgeCount = graph.Edges.Count,
                entryPoints = graph.Nodes.Values.Count(n => n.IsEntryPoint),
                generatedAt = DateTimeOffset.UtcNow
            }
        };

        var json = JsonSerializer.Serialize(payload, Options);
        File.WriteAllText(outputPath, json);
        Console.WriteLine($"  JSON  → {outputPath}");
        return Task.CompletedTask;
    }
}

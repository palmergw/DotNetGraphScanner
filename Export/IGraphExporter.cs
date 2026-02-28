using DotNetGraphScanner.Graph;

namespace DotNetGraphScanner.Export;

public interface IGraphExporter
{
    /// <summary>Export the graph to the specified output path.</summary>
    Task ExportAsync(GraphModel graph, string outputPath, CancellationToken ct = default);
}

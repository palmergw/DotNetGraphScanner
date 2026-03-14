using DotNetGraphScanner.Graph;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Linq;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Top-level coordinator that opens a .sln or .csproj file with Roslyn's
/// MSBuildWorkspace, then runs each analysis pass over every project/document.
/// </summary>
public sealed class SolutionAnalyzer
{
    private readonly ScanOptions _options;

    public SolutionAnalyzer(ScanOptions options) => _options = options;

    public async Task<GraphModel> AnalyzeAsync(CancellationToken ct = default)
    {
        // MSBuild must be located before any MSBuildWorkspace is created
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        var graph = new GraphModel();
        var path = Path.GetFullPath(_options.InputPath);

        Console.WriteLine($"Opening: {path}");

        using var workspace = MSBuildWorkspace.Create();

        // Collect workspace diagnostic info
        workspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine($"  [workspace] {e.Diagnostic.Message}");
        };

        Solution solution;
        if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solutionNodeId = $"solution:{path.ToLowerInvariant()}";
            graph.AddNode(solutionNodeId, Path.GetFileNameWithoutExtension(path),
                NodeKind.Solution, meta: new() { ["path"] = path });

            solution = await workspace.OpenSolutionAsync(path, cancellationToken: ct);

            foreach (var project in solution.Projects)
                await AnalyzeProjectAsync(project, graph, solutionNodeId, ct);
        }
        else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: ct);
            var projNodeId = ProjectNodeId(path);
            graph.AddNode(projNodeId, project.Name, NodeKind.Project, meta: new()
            {
                ["path"] = path,
                ["language"] = project.Language
            });

            // Add to a synthetic root – ID is unique per project so that multiple
            // .csproj pushes don't collapse onto the same node in the database.
            var projName = Path.GetFileNameWithoutExtension(path);
            var rootId   = $"solution:csproj:{projName.ToLowerInvariant()}";
            graph.AddNode(rootId, projName, NodeKind.Solution);
            graph.AddEdge(rootId, projNodeId, EdgeKind.Contains);

            await AnalyzeProjectAsync(project, graph, rootId, ct);
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported input: {path}. Provide a .sln or .csproj file.");
        }

        LinkExternalNodesToPackages(graph);
        return graph;
    }

    // ── Per-project analysis ──────────────────────────────────────────────────

    private async Task AnalyzeProjectAsync(
        Project project,
        GraphModel graph,
        string parentId,
        CancellationToken ct)
    {
        var projId = ProjectNodeId(project.FilePath!);
        graph.AddNode(projId, project.Name, NodeKind.Project, meta: new()
        {
            ["path"] = project.FilePath ?? "",
            ["language"] = project.Language,
            ["assemblyName"] = project.AssemblyName
        });
        graph.AddEdge(parentId, projId, EdgeKind.Contains);

        Console.WriteLine($"  Project: {project.Name} ({project.DocumentIds.Count} documents)");

        // Static csproj analysis (NuGet + project refs) – fast, no compilation needed
        if (!string.IsNullOrEmpty(project.FilePath))
            ProjectFileAnalyzer.Analyze(project.FilePath, projId, graph);

        // Roslyn compilation (needed for semantic analysis)
        Compilation? compilation = null;
        if (_options.AnalyzeCallGraph || _options.AnalyzeDependencies || _options.DetectEntryPoints)
        {
            try
            {
                compilation = await project.GetCompilationAsync(ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [compile error] {project.Name}: {ex.Message}");
            }
        }

        if (compilation is null) return;

        foreach (var doc in project.Documents)
        {
            if (doc.FilePath is null) continue;
            ct.ThrowIfCancellationRequested();

            var tree = await doc.GetSyntaxTreeAsync(ct);
            if (tree is null) continue;

            var model = compilation.GetSemanticModel(tree);

            if (_options.AnalyzeDependencies)
                DependencyAnalyzer.Analyze(tree, model, graph, projId);

            if (_options.DetectEntryPoints)
                EntryPointDetector.Detect(tree, model, graph, projId);

            if (_options.AnalyzeCallGraph)
            {
                var walker = new CallGraphWalker(model, graph, doc.FilePath);
                walker.Visit(await tree.GetRootAsync(ct));
            }
        }
    }

    private static string ProjectNodeId(string path) =>
        $"project:{path.ToLowerInvariant()}";

    /// <summary>
    /// Post-processing: adds Contains edges from NuGet package nodes to any external
    /// node (method, property, type) whose assembly name matches a referenced package.
    /// Only links nodes that don't already have a Contains parent, so that
    /// method-under-type relationships built earlier are preserved.
    /// </summary>
    private static void LinkExternalNodesToPackages(GraphModel graph)
    {
        // Map package-label-lower → nuget node id
        // Package labels match the NuGet/assembly name (e.g. "Microsoft.CodeAnalysis.CSharp")
        var pkgMap = graph.Nodes.Values
            .Where(n => n.Kind == NodeKind.NuGetPackage)
            .ToDictionary(n => n.Label.ToLowerInvariant(), n => n.Id);

        if (pkgMap.Count == 0) return;

        // Build set of node IDs that already have a Contains parent
        var hasContainsParent = graph.Edges
            .Where(e => e.Kind == EdgeKind.Contains)
            .Select(e => e.TargetId)
            .ToHashSet();

        foreach (var node in graph.Nodes.Values.ToList())
        {
            if (!node.Meta.TryGetValue("isExternal", out var ext) || ext != "true") continue;
            if (hasContainsParent.Contains(node.Id)) continue;  // already parented

            // Determine assembly name:
            //   methods/properties: encoded as prefix before '#' in the node ID
            //   types: stored in meta by EnsureExternalTypeNode
            string? asmName = null;
            if (node.Id.Contains('#'))
                asmName = node.Id.Split('#')[0];
            else
                node.Meta.TryGetValue("assemblyName", out asmName);

            if (string.IsNullOrEmpty(asmName)) continue;
            if (!pkgMap.TryGetValue(asmName.ToLowerInvariant(), out var pkgId)) continue;

            graph.AddEdge(pkgId, node.Id, EdgeKind.Contains);
        }
    }
}

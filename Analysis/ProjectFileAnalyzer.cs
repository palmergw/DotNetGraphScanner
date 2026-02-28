using System.Xml.Linq;
using DotNetGraphScanner.Graph;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Parses .csproj XML to extract PackageReference and ProjectReference items and
/// adds corresponding nodes / edges to the graph.
/// </summary>
public static class ProjectFileAnalyzer
{
    public static void Analyze(string csprojPath, string projectNodeId, GraphModel graph)
    {
        if (!File.Exists(csprojPath)) return;

        XDocument doc;
        try { doc = XDocument.Load(csprojPath); }
        catch { return; }

        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // ── NuGet packages ────────────────────────────────────────────────────
        foreach (var pkg in doc.Descendants("PackageReference"))
        {
            var name    = pkg.Attribute("Include")?.Value
                       ?? pkg.Attribute("Update")?.Value;
            var version = pkg.Attribute("Version")?.Value
                       ?? pkg.Element("Version")?.Value
                       ?? "*";

            if (string.IsNullOrWhiteSpace(name)) continue;

            var pkgId = $"nuget:{name.ToLowerInvariant()}";
            graph.AddNode(pkgId, name, NodeKind.NuGetPackage, meta: new()
            {
                ["version"] = version,
                ["url"] = $"https://www.nuget.org/packages/{name}"
            });
            graph.AddEdge(projectNodeId, pkgId, EdgeKind.PackageReference, meta: new()
            {
                ["version"] = version
            });
        }

        // ── Project references ────────────────────────────────────────────────
        foreach (var projRef in doc.Descendants("ProjectReference"))
        {
            var relativePath = projRef.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(relativePath)) continue;

            var dir = Path.GetDirectoryName(csprojPath) ?? ".";
            var absPath = Path.GetFullPath(Path.Combine(dir, relativePath));
            var refName = Path.GetFileNameWithoutExtension(absPath);
            var refId   = $"project:{absPath.ToLowerInvariant()}";

            graph.AddNode(refId, refName, NodeKind.Project, meta: new()
            {
                ["path"] = absPath
            });
            graph.AddEdge(projectNodeId, refId, EdgeKind.ProjectReference);
        }
    }
}

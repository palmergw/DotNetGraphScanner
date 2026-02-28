# dotnet-graph-scanner

A CLI tool that uses **Roslyn** to statically analyse a .NET solution or project and produce an **interactive dependency graph** — mapping entry points, method call chains, type hierarchies, and package references.

---

## Features

| Analysis Pass | What it finds |
|---|---|
| **Entry Points** | `static Main`, ASP.NET Core controllers & actions (`[HttpGet/Post/…]`), Minimal API (`app.MapGet/Post/…`), Azure Functions (`[FunctionName]`) |
| **Call Graph** | Every method-to-method invocation resolved via Roslyn's semantic model |
| **Type Dependencies** | Class inheritance, interface implementations, property ownership |
| **Project References** | `.csproj → .csproj` relationships |
| **NuGet Packages** | `PackageReference` items from every `.csproj` |

### Outputs

| File | Format | Use |
|---|---|---|
| `<name>.graph.html` | Self-contained HTML + D3.js | Open in any browser — interactive force-directed graph |
| `<name>.graph.json` | JSON `{nodes, edges}` | Feed to any downstream tool or custom renderer |
| `<name>.graph.cypher` | Neo4j Cypher | `cypher-shell -f <file>` or paste into Neo4j Browser |

---

## Quick Start

```powershell
# Build
cd DotNetGraphScanner
dotnet build

# Scan a solution
dotnet run -- path/to/MyApp.sln --output ./out

# Scan a single project, with Neo4j Cypher export
dotnet run -- path/to/MyApp.csproj --output ./out --cypher

# Open the result
start ./out/MyApp.graph.html
```

---

## Options

```
Usage: dotnet-graph-scanner <input> [options]

Arguments:
  <input>              Path to a .sln or .csproj file

Options:
  -o, --output <dir>   Output directory (default: current directory)
  --html               Generate HTML visualization [default: true]
  --json               Generate JSON graph file [default: true]
  --cypher             Generate Neo4j Cypher import script [default: false]
  --no-calls           Skip method call graph (faster for huge codebases)
  --no-deps            Skip type dependency analysis
  --no-entry           Skip entry-point detection
  --include-external   Show external (framework/NuGet) nodes in HTML
  -?, -h, --help       Show help
```

---

## HTML Viewer Controls

| Control | Action |
|---|---|
| Click node | Inspect metadata + neighbours in right panel |
| Drag node | Reposition freely |
| Scroll / pinch | Zoom |
| **L** | Toggle labels |
| **F** | Focus search box |
| **Escape** | Deselect |
| Kind filter (top bar) | Show only one node kind |
| Force slider | Adjust repulsion strength |
| Show External toggle | Include/exclude external nodes live |

### Node Colours

| Colour | Kind |
|---|---|
| 🟣 Indigo | Solution |
| 🔵 Blue | Project |
| 🩵 Cyan | Namespace |
| 🟢 Green | Class |
| 🟡 Amber | Interface / Entry-point border |
| 🟪 Purple | Enum |
| 🟠 Orange | NuGet Package |
| ⚫ Slate | Method / Property |

---

## Architecture

```
DotNetGraphScanner/
├── Analysis/
│   ├── ScanOptions.cs           Options passed to analyzer
│   ├── SolutionAnalyzer.cs      Opens .sln/.csproj via MSBuildWorkspace
│   ├── EntryPointDetector.cs    Detects Main, controllers, minimal APIs, Azure Fns
│   ├── CallGraphWalker.cs       CSharpSyntaxWalker → CALLS edges
│   ├── DependencyAnalyzer.cs    Type hierarchy + namespace nodes
│   └── ProjectFileAnalyzer.cs  Parses .csproj for PackageReference/ProjectReference
├── Export/
│   ├── IGraphExporter.cs        Common export interface
│   ├── HtmlExporter.cs          D3.js self-contained HTML output
│   ├── JsonExporter.cs          JSON {nodes, edges} output
│   └── Neo4jCypherExporter.cs  Cypher MERGE statements for Neo4j
├── Graph/
│   ├── GraphModel.cs            In-memory graph (thread-safe reads)
│   ├── GraphNode.cs             Node with id, kind, isEntryPoint, meta
│   ├── GraphEdge.cs             Directed edge with kind, meta
│   ├── NodeKind.cs              Enum of node kinds
│   └── EdgeKind.cs              Enum of edge relationship types
└── Program.cs                   System.CommandLine entry point
```

---

## Extending to a Live Graph DB

The `IGraphExporter` interface makes it straightforward to add a live Neo4j (or any other) connector:

```csharp
// Install: dotnet add package Neo4j.Driver
public sealed class Neo4jBoltExporter : IGraphExporter
{
    public async Task ExportAsync(GraphModel graph, string outputPath, CancellationToken ct)
    {
        using var driver = GraphDatabase.Driver("bolt://localhost:7687",
            AuthTokens.Basic("neo4j", "password"));
        await using var session = driver.AsyncSession();
        foreach (var node in graph.Nodes.Values)
            await session.RunAsync("MERGE (n:Node {id:$id}) SET n += $props",
                new { id = node.Id, props = node.Meta });
        // … edges …
    }
}
```

Then register it alongside `HtmlExporter` / `JsonExporter` in `Program.cs`.

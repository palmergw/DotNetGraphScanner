# dotnet-graph-scanner

A CLI tool that uses **Roslyn** to statically analyse a .NET solution or project and produce an **interactive dependency graph** — mapping entry points, method call chains, type hierarchies, and package references.

Also supports **full-graph database storage**: scan individual APIs, push the complete code graph plus cross-API metadata to a [Memgraph](https://memgraph.com/) (or Neo4j-compatible) database, and explore everything in a **live unified UI** — or query impact from the CLI using file paths, function names, or git commits.

---

## Features

| Analysis Pass | What it finds |
|---|---|
| **Entry Points** | `static Main`, ASP.NET Core controllers & actions (`[HttpGet/Post/…]`), Minimal API (`app.MapGet/Post/…`), Azure Functions (`[FunctionName]`) |
| **Call Graph** | Every method-to-method invocation resolved via Roslyn's semantic model |
| **Type Dependencies** | Class inheritance, interface implementations, property ownership |
| **Project References** | `.csproj → .csproj` relationships |
| **NuGet Packages** | `PackageReference` items from every `.csproj` |
| **Cross-API Calls** | Outbound HTTP calls between APIs annotated with `[ApiCall]` |
| **Source Location** | `filePath` + `lineStart` stored on every method node for git-to-graph mapping |

### Outputs

| File | Format | Use |
|---|---|---|
| `<name>.graph.html` | Self-contained HTML + D3.js | Open in any browser — interactive force-directed graph |
| `<name>.graph.json` | JSON `{nodes, edges}` | Feed to any downstream tool or custom renderer |
| `<name>.graph.cypher` | Neo4j Cypher | `cypher-shell -f <file>` or paste into Neo4j Browser |
| `cross-api-live.html` | Self-contained HTML | Live unified explorer — three views, queries the database in your browser |

---

## Quick Start

```powershell
# Build
cd DotNetGraphScanner
dotnet build

# Scan a solution
dotnet run -- path/to/MyApp.sln --output ./out

# Scan a project and push the full code graph + cross-API metadata to the database
dotnet run -- scan path/to/MyApi.csproj --output ./out --push

# Re-render an existing JSON without re-scanning
dotnet run -- render ./out/MyApp.graph.json --output ./out

# Generate the unified live explorer HTML
dotnet run -- cross-view --output ./out

# Find which endpoints are affected by a changed file
dotnet run -- impact --file WeatherController.cs

# Find impact of a git commit
dotnet run -- impact --commit a1b2c3d --repo ../MyRepo

# Open the results
start ./out/MyApp.graph.html
start ./out/cross-api-live.html
```

---

## Cross-API Mapping with Full Code Graph

### How it works

1. **Annotate outbound calls** — add `[ApiCall("TargetApi", "VERB /route")]` to the methods of your typed HTTP-client interfaces:

   ```csharp
   // In FooApi: clients/IBarApiClient.cs
   using ApiContracts;

   public interface IBarApiClient
   {
       [ApiCall("BarApi", "GET /inventory")]
       Task<InventoryResponse> GetInventoryAsync(string productId);

       [ApiCall("BarApi", "POST /notifications")]
       Task<NotifyResponse> SendNotificationAsync(string email, string message);
   }
   ```

2. **Scan each API individually** with `--push`. This writes:
   - **Full code graph** — every `CodeNode` (Class, Method, Namespace, etc.) with `filePath` + `lineStart` and all structural edges (`CALLS`, `CONTAINS`, `INHERITS`, etc.)
  - **Canonical dependency metadata** — `Api` summary nodes, entry-point and outbound-call flags on `CodeNode`, canonical `CodeNode -> CodeNode RESOLVES_TO` connections, and enough structural edges to compute reachability directly from the stored graph

3. **Open the unified live explorer** — run `cross-view` once to generate `cross-api-live.html`. Open it in a browser, connect to the database, and switch between views using the dropdown in the connection bar.

4. **Run impact analysis from the CLI** — use the `impact` subcommand to find which HTTP endpoints are reachable from a changed file, function, or git commit.

### Live Explorer Views

The `cross-api-live.html` page has three views selectable from a dropdown in the connection bar:

| View | What it shows |
|---|---|
| **🔗 API Dependencies** | Cross-API card layout — entry points, outbound calls, resolved connections, BFS impact panel. Click any pill to highlight its connections. |
| **🕸 Code Graph** | Per-API D3 force-directed canvas — select an API from the dropdown to render its full code graph. Filter by node kind or search by label. Click a node for file/line metadata. |
| **🎯 Impact Explorer** | Enter a file path fragment, function name, or paste a resolved file list to find affected HTTP endpoints across all APIs. For git commit analysis, use the `impact` CLI subcommand instead. |

Implementation note:
The live explorer derives API Dependency cards from `CodeNode`, prefers stored `CodeNode -> CodeNode RESOLVES_TO` for connection rendering, and uses canonical virtual-dispatch-aware BFS for impact mapping. `Api` summary nodes are used for scan metadata when present, but the dependency view can still fall back to `CodeNode.apiName` and `CodeNode.scannedAt` if only `CodeNode` data is available.

### `scan --push` options

```
Usage: dotnet-graph-scanner scan <input> [options]

Arguments:
  <input>                  Path to a .sln or .csproj file

Core options:
  -o, --output <dir>       Output directory (default: current directory)
  --html                   Generate HTML visualization [default: true]
  --json                   Generate JSON graph file [default: true]
  --cypher                 Generate Neo4j Cypher import script [default: false]
  --no-calls               Skip method call graph (faster for huge codebases)
  --no-deps                Skip type dependency analysis
  --no-entry               Skip entry-point detection
  --include-external       Show external (framework/NuGet) nodes in HTML

Cross-API / database options:
  --push                   Push full code graph + canonical API dependency metadata to the database
  --neo4j-url <url>        Bolt URL of the database [default: bolt://127.0.0.1:7687]
  --neo4j-user <user>      Database username [default: neo4j]
  --neo4j-pass <pass>      Database password
```

### `cross-view` command

```
Usage: dotnet-graph-scanner cross-view [options]

Options:
  -o, --output <dir>       Output directory for cross-api-live.html [default: current directory]
  --neo4j-url <url>        Pre-fill the bolt URL in the viewer [default: bolt://127.0.0.1:7687]
  --neo4j-user <user>      Pre-fill the username in the viewer [default: neo4j]
  --neo4j-pass <pass>      (Not embedded in the page — for connectivity check only)
  -?, -h, --help           Show help
```

### `impact` command

Finds HTTP entry points reachable from changed code by traversing `CALLS` edges in the stored code graph. Requires `scan --push` to have been run first.

```
Usage: dotnet-graph-scanner impact [options]

Options:
  --file <fragment>          File path fragment to match (e.g. WeatherController.cs)
  --function <name>          Method name fragment to match (e.g. GetWeatherForecast)
  --commit <sha>             Git commit SHA — resolves changed files and functions via git diff
  --commit-range <from..to>  Git commit range (e.g. abc123..def456)
  --repo <path>              Git repository root used with --commit / --commit-range [default: .]
  --api <name>               Filter results to a specific API name
  --neo4j-url <url>          Bolt URL of the database [default: bolt://127.0.0.1:7687]
  --neo4j-user <user>        Database username [default: neo4j]
  --neo4j-pass <pass>        Database password
```

**Examples:**

```powershell
# Which endpoints are affected if WeatherController.cs changes?
dotnet run -- impact --file WeatherController.cs

# Which endpoints call into a specific method?
dotnet run -- impact --function ProcessOrder

# Which endpoints are affected by the changes in a commit?
dotnet run -- impact --commit a1b2c3d --repo ../MyRepo

# Same, scoped to one API
dotnet run -- impact --commit-range main~5..main --repo ../MyRepo --api OrdersApi
```

For `--commit` / `--commit-range`, the tool shells out to `git diff --name-only` to resolve changed `.cs` files, then parses hunk context headers (`@@ … @@ MethodName`) to also extract changed function names. No git library dependency required.

### Sample APIs

The `samples/` directory contains two example APIs that demonstrate the feature:

| API | Entry Points | Calls into |
|---|---|---|
| **FooApi** | `GET /orders`, `POST /orders`, `DELETE /orders/{id}`, `GET /orders/{id}`, `GET /health` | BarApi: inventory, notifications, status |
| **BarApi** | `GET /inventory`, `POST /inventory/reserve`, `POST /notifications`, `GET /notifications/{id}`, `GET /status` | FooApi: orders, health |

```
samples/
├── ApiContracts/     [ApiCall] attribute definition (shared library)
├── FooApi/           Order-management API (references BarApi via typed client)
└── BarApi/           Inventory & notification API (references FooApi via typed client)
```

Scan and push both, then open the live viewer:

```powershell
dotnet run -- scan samples/FooApi/FooApi.csproj --output out-db --push
dotnet run -- scan samples/BarApi/BarApi.csproj --output out-db --push
dotnet run -- cross-view --output out-db
start out-db/cross-api-live.html
```

### Database setup

The tool is tested against **Memgraph** via Docker:

```powershell
# docker-compose.yml is in the workspace root
docker compose up -d
# Memgraph bolt  → bolt://127.0.0.1:7687
# Memgraph Lab UI → http://localhost:3000
```

Any Neo4j-compatible database that supports the Bolt protocol and openCypher will work.

---

## `render` command

Re-renders the HTML viewer from an existing `.graph.json` without re-running analysis. Useful after upgrading the viewer or changing visual options.

```powershell
dotnet run -- render ./out/MyApp.graph.json --output ./out
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
│   ├── ScanOptions.cs              Options passed to the analyzer
│   ├── SolutionAnalyzer.cs         Opens .sln/.csproj via MSBuildWorkspace
│   ├── EntryPointDetector.cs       Detects Main, controllers, minimal APIs, Azure Fns, [ApiCall]
│   ├── CallGraphWalker.cs          CSharpSyntaxWalker → CALLS edges; records filePath + lineStart
│   ├── DependencyAnalyzer.cs       Type hierarchy + namespace nodes; records filePath on type nodes
│   ├── ProjectFileAnalyzer.cs      Parses .csproj for PackageReference/ProjectReference
│   └── CrossApiExtractor.cs        Shared route-matching and virtual-dispatch helpers for canonical dependency analysis
├── Export/
│   ├── IGraphExporter.cs           Common export interface
│   ├── HtmlExporter.cs             D3.js self-contained HTML output (force graph + hierarchy)
│   ├── JsonExporter.cs             JSON {nodes, edges} output
│   ├── Neo4jCypherExporter.cs      Cypher MERGE statements for Neo4j/Memgraph
│   └── UnifiedLiveHtmlExporter.cs  Live unified explorer HTML — three views, queries DB in browser
├── Store/
│   └── Neo4jGraphStore.cs          Bolt persistence: full code graph push, impact query, resolve connections
├── Graph/
│   ├── GraphModel.cs               In-memory graph (thread-safe reads)
│   ├── GraphNode.cs                Node with id, kind, isEntryPoint, meta (incl. filePath, lineStart)
│   ├── GraphEdge.cs                Directed edge with kind, meta
│   ├── NodeKind.cs                 Enum of node kinds
│   └── EdgeKind.cs                 Enum of edge relationship types (incl. ExternalApiCall)
└── Program.cs                      System.CommandLine entry point (scan, render, cross-view, impact)

samples/
├── ApiContracts/               Shared [ApiCall] attribute
├── FooApi/                     Example order-management API
│   ├── Controllers/            HTTP endpoints (Orders, Health)
│   ├── Services/               Internal service layer (calls IBarApiClient)
│   └── Clients/                IBarApiClient + BarApiClient ([ApiCall] annotated)
└── BarApi/                     Example inventory/notification API
    ├── Controllers/            HTTP endpoints (Inventory, Notifications, Status)
    ├── Services/               Internal service layer (calls IFooApiClient)
    └── Clients/                IFooApiClient + FooApiClient ([ApiCall] annotated)
```

### Graph schema in the database

**Summary metadata**:

| Node | Key property | Description |
|---|---|---|
| `(:Api)` | `name` | One node per scanned project; carries `scannedAt` timestamp for summaries and diagnostics |

**Full code graph** (written by `--push`, used by the Code Graph view, API Dependency view, and `impact` command):

| Node | Labels | Key properties |
|---|---|---|
| `(:CodeNode:Method)` | `CodeNode` + NodeKind | `id`, `label`, `kind`, `apiName`, `filePath`, `lineStart`, `fullName`, `isEntryPoint`, `httpMethod`, `routeTemplate`, `isApiCall`, `targetApi`, `targetRoute` |
| `(:CodeNode:Class)` | `CodeNode` + NodeKind | `id`, `label`, `kind`, `apiName`, `filePath`, `namespace`, `fullName` |
| `(:CodeNode:<Kind>)` | `CodeNode` + NodeKind | All meta key/value pairs from the in-memory graph flattened as properties, plus `scannedAt` |

| Relationship | Description |
|---|---|
| `CALLS` | Method invocation |
| `CONTAINS` | Parent → child containment (Project → Class → Method, etc.) |
| `INHERITS` | Class inheritance |
| `IMPLEMENTS` | Interface implementation |
| `ACCESSES` | Method reads a property or field |
| `PROJECT_REFERENCE` | Project → Project dependency |
| `PACKAGE_REFERENCE` | Project → NuGet package |
| `ENTRY_POINT` | Project/parent → entry-point node |
| `RESOLVES_TO` | Outbound-call method → entry-point method across APIs |
| `USES_ATTRIBUTE` | Type or method decorated with an attribute class |
| `EXTERNAL_API_CALL` | Method calls an external API endpoint |

The `apiName` property on every `CodeNode` scopes cleanup, and the dependency view derives its cards, connectors, and impact paths directly from `CodeNode` plus structural edges. Re-scanning an API deletes and rewrites only its own nodes, leaving other APIs untouched.

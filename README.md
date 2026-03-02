# dotnet-graph-scanner

A CLI tool that uses **Roslyn** to statically analyse a .NET solution or project and produce an **interactive dependency graph** — mapping entry points, method call chains, type hierarchies, and package references.

Also supports **cross-API dependency mapping**: scan individual APIs, push their metadata to a [Memgraph](https://memgraph.com/) (or Neo4j-compatible) database, and open a **live UI** that always shows the latest cross-API connections.

---

## Interactive Preview

▶ **[Open cross-API map (FooApi ↔ BarApi)](https://htmlpreview.github.io/?https://github.com/palmergw/DotNetGraphScanner/blob/main/sample-output/FooApi-BarApi.cross.html)**

Static snapshot of the FooApi ↔ BarApi dependency map generated from the bundled sample projects. Shows entry points, outbound cross-API calls, and BFS-reachability impact analysis. Click any box to explore.

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

### Outputs

| File | Format | Use |
|---|---|---|
| `<name>.graph.html` | Self-contained HTML + D3.js | Open in any browser — interactive force-directed graph |
| `<name>.graph.json` | JSON `{nodes, edges}` | Feed to any downstream tool or custom renderer |
| `<name>.graph.cypher` | Neo4j Cypher | `cypher-shell -f <file>` or paste into Neo4j Browser |
| `cross-api-live.html` | Self-contained HTML | Live cross-API dependency map — queries the database in your browser |

---

## Quick Start

```powershell
# Build
cd DotNetGraphScanner
dotnet build

# Scan a solution
dotnet run -- path/to/MyApp.sln --output ./out

# Scan a project and push cross-API metadata to the database
dotnet run -- scan path/to/MyApi.csproj --output ./out --push

# Re-render an existing JSON without re-scanning
dotnet run -- render ./out/MyApp.graph.json --output ./out

# Generate the live cross-API viewer HTML
dotnet run -- cross-view --output ./out

# Open the results
start ./out/MyApp.graph.html
start ./out/cross-api-live.html
```

---

## Cross-API Mapping

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

2. **Scan each API individually** — pass `--push` to write the entry points, outbound calls, and internal impact map to the database. Each push also re-resolves all `RESOLVES_TO` edges across every API already in the database, so connections are always current.

3. **Open the live viewer** — run `cross-view` once to generate `cross-api-live.html`. Open it in a browser, enter your database password, and click **Connect**. The page queries the database directly — hit **Refresh** at any time to pick up new scans without regenerating the file.

4. **Impact analysis** — click any entry point to see every outbound API call that could be triggered from it. Click any outbound call to see which entry points on its own API can cause it, and which entry point on the target API it resolves to.

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
  --push                   Push cross-API metadata to the database after scanning
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
│   ├── CallGraphWalker.cs          CSharpSyntaxWalker → CALLS edges
│   ├── DependencyAnalyzer.cs       Type hierarchy + namespace nodes
│   ├── ProjectFileAnalyzer.cs      Parses .csproj for PackageReference/ProjectReference
│   └── CrossApiExtractor.cs        Extracts per-API cross-API metadata (EPs, outbound calls, BFS impact)
├── Export/
│   ├── IGraphExporter.cs           Common export interface
│   ├── HtmlExporter.cs             D3.js self-contained HTML output
│   ├── JsonExporter.cs             JSON {nodes, edges} output
│   ├── Neo4jCypherExporter.cs      Cypher MERGE statements for Neo4j/Memgraph
│   ├── CrossApiHtmlExporter.cs     Static API-box visual with impact analysis
│   └── CrossApiLiveHtmlExporter.cs Live viewer HTML — queries the database in the browser
├── Store/
│   └── Neo4jGraphStore.cs          Bolt persistence: push API, resolve connections, clear
├── Graph/
│   ├── GraphModel.cs               In-memory graph (thread-safe reads)
│   ├── GraphNode.cs                Node with id, kind, isEntryPoint, meta
│   ├── GraphEdge.cs                Directed edge with kind, meta
│   ├── NodeKind.cs                 Enum of node kinds
│   ├── EdgeKind.cs                 Enum of edge relationship types (incl. ExternalApiCall)
│   └── CrossApiModel.cs            Types: EntrypointInfo, ApiCallInfo, SingleApiCrossInfo, …
└── Program.cs                      System.CommandLine entry point (scan, render, cross-view)

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

| Node | Key property | Description |
|---|---|---|
| `(:Api)` | `name` | One node per scanned project; carries `scannedAt` timestamp |
| `(:EntryPoint)` | `nodeId` | HTTP entry point; carries `httpMethod`, `route`, `apiName` |
| `(:OutboundCall)` | `nodeId` | An `[ApiCall]`-annotated method; carries `targetApi`, `targetRoute`, `ownerApi` |

| Relationship | From → To | Description |
|---|---|---|
| `HAS_ENTRY_POINT` | Api → EntryPoint | Ownership |
| `HAS_OUTBOUND_CALL` | Api → OutboundCall | Ownership |
| `CAN_REACH` | EntryPoint → OutboundCall | BFS reachability via internal call graph |
| `RESOLVES_TO` | OutboundCall → EntryPoint | Route-matched cross-API connection |

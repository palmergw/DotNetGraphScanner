using System.CommandLine;
using DotNetGraphScanner.Analysis;
using DotNetGraphScanner.Export;
using DotNetGraphScanner.Graph;
using DotNetGraphScanner.Store;

// ── Must register MSBuild BEFORE any Roslyn types are loaded ─────────────────
// (done inside SolutionAnalyzer when first invoked)

// ── Shared options ────────────────────────────────────────────────────────────
var outputOpt = new Option<string>(
    aliases: ["--output", "-o"],
    description: "Output directory for generated files.",
    getDefaultValue: () => ".");

// ════════════════════════════════════════════════════════════════════════════════
// 'scan' subcommand  (was the root command; now named explicitly)
// ════════════════════════════════════════════════════════════════════════════════
var scanInputArg = new Argument<string>(
    name: "input",
    description: "Path to a .sln or .csproj file to analyze.");

var htmlOpt = new Option<bool>(
    aliases: ["--html"],
    description: "Generate an interactive HTML visualization (default: true).",
    getDefaultValue: () => true);

var jsonOpt = new Option<bool>(
    aliases: ["--json"],
    description: "Generate a JSON graph file (default: true).",
    getDefaultValue: () => true);

var neo4jOpt = new Option<bool>(
    aliases: ["--cypher"],
    description: "Generate a Neo4j Cypher import script.",
    getDefaultValue: () => false);

var noCallsOpt = new Option<bool>(
    aliases: ["--no-calls"],
    description: "Skip method call graph analysis (faster for large solutions).",
    getDefaultValue: () => false);

var noDepsOpt = new Option<bool>(
    aliases: ["--no-deps"],
    description: "Skip class/interface dependency analysis.",
    getDefaultValue: () => false);

var noEntryOpt = new Option<bool>(
    aliases: ["--no-entry"],
    description: "Skip entry-point detection.",
    getDefaultValue: () => false);

var externalOpt = new Option<bool>(
    aliases: ["--include-external"],
    description: "Include external (non-project) type and method nodes in the HTML.",
    getDefaultValue: () => false);

// ── Neo4j push options (shared by scan + cross-view) ────────────────────────
var neoUrlOpt  = new Option<string>(
    aliases: ["--neo4j-url"],
    description: "Bolt URL of the Neo4j-compatible database.",
    getDefaultValue: () => "bolt://127.0.0.1:7687");

var neoUserOpt = new Option<string?>(
    aliases: ["--neo4j-user"],
    description: "Neo4j username (leave empty for no auth).",
    getDefaultValue: () => "neo4j");

var neoPassOpt = new Option<string?>(
    aliases: ["--neo4j-pass"],
    description: "Neo4j password (leave empty for no auth).",
    getDefaultValue: () => null);

var pushOpt = new Option<bool>(
    aliases: ["--push"],
    description: "Push cross-API metadata to a Neo4j database after scanning.",
    getDefaultValue: () => false);

var scanCmd = new Command("scan", "Analyze a .NET solution/project and produce graph files.")
{
    scanInputArg, outputOpt, htmlOpt, jsonOpt, neo4jOpt,
    noCallsOpt, noDepsOpt, noEntryOpt, externalOpt,
    pushOpt, neoUrlOpt, neoUserOpt, neoPassOpt
};

scanCmd.SetHandler(async (ctx) =>
{
    var input           = ctx.ParseResult.GetValueForArgument(scanInputArg);
    var output          = ctx.ParseResult.GetValueForOption(outputOpt)!;
    var genHtml         = ctx.ParseResult.GetValueForOption(htmlOpt);
    var genJson         = ctx.ParseResult.GetValueForOption(jsonOpt);
    var genCypher       = ctx.ParseResult.GetValueForOption(neo4jOpt);
    var noCalls         = ctx.ParseResult.GetValueForOption(noCallsOpt);
    var noDeps          = ctx.ParseResult.GetValueForOption(noDepsOpt);
    var noEntry         = ctx.ParseResult.GetValueForOption(noEntryOpt);
    var includeExternal = ctx.ParseResult.GetValueForOption(externalOpt);

    if (!File.Exists(input))
    {
        Console.Error.WriteLine($"Error: file not found – {input}");
        ctx.ExitCode = 1;
        return;
    }

    Directory.CreateDirectory(output);

    var baseName = Path.GetFileNameWithoutExtension(input);
    var cts      = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var options = new ScanOptions
    {
        InputPath            = input,
        OutputDir            = output,
        DetectEntryPoints    = !noEntry,
        AnalyzeCallGraph     = !noCalls,
        AnalyzeDependencies  = !noDeps,
        IncludeExternalNodes = includeExternal
    };

    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine("  dotnet-graph-scanner");
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine($"  Input   : {Path.GetFullPath(input)}");
    Console.WriteLine($"  Output  : {Path.GetFullPath(output)}");
    Console.WriteLine($"  Options : calls={!noCalls} deps={!noDeps} entry={!noEntry}");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();

    var analyzer = new SolutionAnalyzer(options);
    var graph    = await analyzer.AnalyzeAsync(cts.Token);

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"  Analysis complete in {sw.Elapsed.TotalSeconds:F1}s");
    graph.PrintStats();
    Console.WriteLine();

    Console.WriteLine("Exporting…");

    if (genJson)
    {
        var jsonPath = Path.Combine(output, $"{baseName}.graph.json");
        await new JsonExporter().ExportAsync(graph, jsonPath, cts.Token);
    }

    if (genHtml)
    {
        var htmlPath = Path.Combine(output, $"{baseName}.graph.html");
        await new HtmlExporter(includeExternal).ExportAsync(graph, htmlPath, cts.Token);
    }

    if (genCypher)
    {
        var cypherPath = Path.Combine(output, $"{baseName}.graph.cypher");
        await new Neo4jCypherExporter().ExportAsync(graph, cypherPath, cts.Token);
    }

    if (ctx.ParseResult.GetValueForOption(pushOpt))
    {
        var neoUrl  = ctx.ParseResult.GetValueForOption(neoUrlOpt)!;
        var neoUser = ctx.ParseResult.GetValueForOption(neoUserOpt);
        var neoPass = ctx.ParseResult.GetValueForOption(neoPassOpt);

        Console.WriteLine("Pushing cross-API metadata to Neo4j…");
        try
        {
            await using var store = new Neo4jGraphStore(neoUrl, neoUser, neoPass);
            await store.VerifyConnectivityAsync();
            await store.EnsureConstraintsAsync();
            var info = CrossApiExtractor.Extract(graph, baseName);
            await store.PushApiAsync(info, cts.Token);
            Console.WriteLine($"  Pushed {info.EntryPoints.Count} entry points, {info.OutboundCalls.Count} outbound calls.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Neo4j push failed: {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine("════════════════════════════════════════════════");
});

// ════════════════════════════════════════════════════════════════════════════════
// 'render' subcommand  – re-render the HTML viewer from an existing .graph.json
//                        without re-running analysis.
// ════════════════════════════════════════════════════════════════════════════════
var renderInputArg = new Argument<string>(
    name: "json",
    description: "Path to an existing .graph.json file produced by the 'scan' command.");

var renderCmd = new Command("render", "Re-render the HTML viewer from an existing .graph.json file.")
{
    renderInputArg, outputOpt
};

renderCmd.SetHandler(async (ctx) =>
{
    var jsonPath = ctx.ParseResult.GetValueForArgument(renderInputArg);
    var output   = ctx.ParseResult.GetValueForOption(outputOpt)!;

    if (!File.Exists(jsonPath))
    {
        Console.Error.WriteLine($"Error: file not found – {jsonPath}");
        ctx.ExitCode = 1;
        return;
    }

    Directory.CreateDirectory(output);

    var baseName = Path.GetFileNameWithoutExtension(jsonPath);
    // Strip trailing ".graph" if present so the HTML is named consistently.
    if (baseName.EndsWith(".graph", StringComparison.OrdinalIgnoreCase))
        baseName = baseName[..^".graph".Length];

    var htmlPath = Path.Combine(output, $"{baseName}.graph.html");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine("  dotnet-graph-scanner render");
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine($"  JSON    : {Path.GetFullPath(jsonPath)}");
    Console.WriteLine($"  Output  : {Path.GetFullPath(htmlPath)}");
    Console.WriteLine();

    await HtmlExporter.RenderFromJsonAsync(jsonPath, htmlPath, cts.Token);

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine("════════════════════════════════════════════════");
});

// ════════════════════════════════════════════════════════════════════════════════
// 'cross-view' subcommand  – generate a live HTML page that pulls cross-API data
//                            from a Neo4j-compatible database at browser runtime
// ════════════════════════════════════════════════════════════════════════════════
var crossViewCmd = new Command("cross-view",
    "Generate a live HTML page that visualises cross-API data stored in Neo4j.")
{
    outputOpt, neoUrlOpt, neoUserOpt, neoPassOpt
};

crossViewCmd.SetHandler(async (ctx) =>
{
    var output  = ctx.ParseResult.GetValueForOption(outputOpt)!;
    var neoUrl  = ctx.ParseResult.GetValueForOption(neoUrlOpt)!;
    var neoUser = ctx.ParseResult.GetValueForOption(neoUserOpt);
    var _       = ctx.ParseResult.GetValueForOption(neoPassOpt);  // not embedded in page

    Directory.CreateDirectory(output);

    var htmlPath = Path.Combine(output, "cross-api-live.html");
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine("  dotnet-graph-scanner cross-view");
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine($"  Output  : {Path.GetFullPath(htmlPath)}");
    Console.WriteLine($"  Neo4j   : {neoUrl}");
    Console.WriteLine();

    await new CrossApiLiveHtmlExporter(
        boltUrl: neoUrl,
        defaultUser: neoUser ?? "neo4j"
    ).ExportAsync(htmlPath, cts.Token);

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine("════════════════════════════════════════════════");
});

// ════════════════════════════════════════════════════════════════════════════════
// Root command
// ════════════════════════════════════════════════════════════════════════════════
var rootCmd = new RootCommand("dotnet-graph-scanner – map a .NET codebase into a dependency graph")
{
    scanCmd,
    renderCmd,
    crossViewCmd,
};

// Keep the old behaviour: if the first argument looks like a .sln/.csproj path
// (not a subcommand name), forward transparently to 'scan' so existing scripts
// don't break.
if (args.Length > 0 && args[0] is not ("scan" or "render" or "cross-view" or "--help" or "-h" or "--version"))
    args = ["scan", .. args];

return await rootCmd.InvokeAsync(args);

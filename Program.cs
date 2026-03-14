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
            await store.PushApiAsync(info, graph, cts.Token);
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

    await new UnifiedLiveHtmlExporter(
        boltUrl: neoUrl,
        defaultUser: neoUser ?? "neo4j"
    ).ExportAsync(htmlPath, cts.Token);

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine("════════════════════════════════════════════════");
});

// ════════════════════════════════════════════════════════════════════════════════
// 'impact' subcommand  – find HTTP endpoints affected by changed code
// ════════════════════════════════════════════════════════════════════════════════
var impFileOpt = new Option<string?>(
    aliases: ["--file"],
    description: "File path fragment to match against CodeNode.filePath.");

var impFnOpt = new Option<string?>(
    aliases: ["--function"],
    description: "Function/method name fragment to match against CodeNode.label.");

var impCommitOpt = new Option<string?>(
    aliases: ["--commit"],
    description: "Git commit SHA. Changed files and functions are resolved via git diff.");

var impRangeOpt = new Option<string?>(
    aliases: ["--commit-range"],
    description: "Git commit range (e.g. abc123..def456).");

var impRepoOpt = new Option<string>(
    aliases: ["--repo"],
    description: "Path to the git repository root (used with --commit / --commit-range).",
    getDefaultValue: () => ".");

var impApiOpt = new Option<string?>(
    aliases: ["--api"],
    description: "Filter results to a specific API name.");

var impCmd = new Command("impact",
    "Find HTTP entry points affected by changed files, functions, or git commits.")
{
    impFileOpt, impFnOpt, impCommitOpt, impRangeOpt, impRepoOpt, impApiOpt,
    neoUrlOpt, neoUserOpt, neoPassOpt
};

impCmd.SetHandler(async (ctx) =>
{
    var file        = ctx.ParseResult.GetValueForOption(impFileOpt);
    var fn          = ctx.ParseResult.GetValueForOption(impFnOpt);
    var commit      = ctx.ParseResult.GetValueForOption(impCommitOpt);
    var commitRange = ctx.ParseResult.GetValueForOption(impRangeOpt);
    var repo        = ctx.ParseResult.GetValueForOption(impRepoOpt)!;
    var apiFilter   = ctx.ParseResult.GetValueForOption(impApiOpt);
    var neoUrl      = ctx.ParseResult.GetValueForOption(neoUrlOpt)!;
    var neoUser     = ctx.ParseResult.GetValueForOption(neoUserOpt);
    var neoPass     = ctx.ParseResult.GetValueForOption(neoPassOpt);

    var filePaths = new List<string>();
    var fnNames   = new List<string>();

    if (file is not null) filePaths.Add(file);
    if (fn   is not null) fnNames.Add(fn);

    // Git resolution for --commit / --commit-range
    if (commit is not null || commitRange is not null)
    {
        var range    = commitRange ?? $"{commit}^..{commit}";
        var repoPath = Path.GetFullPath(repo);

        Console.WriteLine($"  Resolving git diff: {range} in {repoPath}");

        var diffFiles = await RunGitAsync(repoPath, $"diff --name-only {range}");
        foreach (var f in diffFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = f.Trim();
            if (!string.IsNullOrEmpty(trimmed) &&
                trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                // Normalise to OS path separator: git always emits '/' but the
                // DB stores paths with Path.DirectorySeparatorChar ('\' on Windows).
                filePaths.Add(trimmed.Replace('/', Path.DirectorySeparatorChar));
        }

        // Extract changed function names from hunk context lines (@@ ... @@ MethodName)
        foreach (var csFile in filePaths.ToList())
        {
            // git always expects forward slashes; restore them for the command argument.
            var gitPath = csFile.Replace(Path.DirectorySeparatorChar, '/');
            var diffText = await RunGitAsync(repoPath, $"diff -U0 {range} -- \"{gitPath}\"");
            foreach (var line in diffText.Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"^@@ [^@]+ @@ (.+)$");
                if (!m.Success) continue;
                // Extract identifier before first '(' — usually the method name
                var ident = System.Text.RegularExpressions.Regex.Match(
                    m.Groups[1].Value, @"(\w+)\s*[\.(<]");
                if (ident.Success)
                    fnNames.Add(ident.Groups[1].Value);
            }
        }

        Console.WriteLine(
            $"  Resolved {filePaths.Count} changed .cs file(s), " +
            $"{fnNames.Count} changed function(s)");
    }

    if (filePaths.Count == 0 && fnNames.Count == 0)
    {
        Console.Error.WriteLine(
            "Nothing to query. Provide --file, --function, --commit, or --commit-range.");
        ctx.ExitCode = 1;
        return;
    }

    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine("  dotnet-graph-scanner impact");
    Console.WriteLine("════════════════════════════════════════════════");
    if (filePaths.Count > 0)
        Console.WriteLine($"  Files     : {string.Join(", ", filePaths)}");
    if (fnNames.Count > 0)
        Console.WriteLine($"  Functions : {string.Join(", ", fnNames.Distinct())}");
    if (apiFilter is not null)
        Console.WriteLine($"  API filter: {apiFilter}");
    Console.WriteLine();

    try
    {
        await using var store = new DotNetGraphScanner.Store.Neo4jGraphStore(neoUrl, neoUser, neoPass);
        await store.VerifyConnectivityAsync();
        var results = await store.QueryImpactAsync(
            filePaths, fnNames.Distinct().ToList(), apiFilter);

        if (results.Count == 0)
        {
            Console.WriteLine("  No affected endpoints found.");
        }
        else
        {
            Console.WriteLine($"  {"API",-22} {"Method",-8} {"Route",-45} Label");
            Console.WriteLine($"  {new string('-', 22)} {new string('-', 8)} {new string('-', 45)} {new string('-', 20)}");
            foreach (var (api, method, route, label) in results)
                Console.WriteLine($"  {api,-22} {method,-8} {route,-45} {label}");
            Console.WriteLine();
            Console.WriteLine($"  {results.Count} affected endpoint(s) found.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Impact query failed: {ex.Message}");
        ctx.ExitCode = 1;
    }

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine("════════════════════════════════════════════════");
});

// ════════════════════════════════════════════════════════════════════════════════
// 'diagnostic' subcommand  – query the live DB and report what's stored
// ════════════════════════════════════════════════════════════════════════════════
var diagCmd = new Command("diagnostic",
    "Query the Neo4j database and print a diagnostic report of what's captured.")
{
    neoUrlOpt, neoUserOpt, neoPassOpt
};

diagCmd.SetHandler(async (ctx) =>
{
    var neoUrl  = ctx.ParseResult.GetValueForOption(neoUrlOpt)!;
    var neoUser = ctx.ParseResult.GetValueForOption(neoUserOpt);
    var neoPass = ctx.ParseResult.GetValueForOption(neoPassOpt);

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Console.WriteLine();
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine("  dotnet-graph-scanner diagnostic");
    Console.WriteLine("════════════════════════════════════════════════");
    Console.WriteLine($"  Neo4j   : {neoUrl}");

    try
    {
        await using var store = new DotNetGraphScanner.Store.Neo4jGraphStore(neoUrl, neoUser, neoPass);
        await store.VerifyConnectivityAsync();
        await store.RunDiagnosticAsync(cts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Diagnostic failed: {ex.Message}");
        ctx.ExitCode = 1;
    }
});

// ════════════════════════════════════════════════════════════════════════════════
// Root command
// ════════════════════════════════════════════════════════════════════════════════
var rootCmd = new RootCommand("dotnet-graph-scanner – map a .NET codebase into a dependency graph")
{
    scanCmd,
    renderCmd,
    crossViewCmd,
    impCmd,
    diagCmd,
};

// Keep the old behaviour: if the first argument looks like a .sln/.csproj path
// (not a subcommand name), forward transparently to 'scan' so existing scripts
// don't break.
if (args.Length > 0 && args[0] is not ("scan" or "render" or "cross-view" or "impact" or "diagnostic" or "--help" or "-h" or "--version"))
    args = ["scan", .. args];

return await rootCmd.InvokeAsync(args);

// ── Git helper ────────────────────────────────────────────────────────────────
static async Task<string> RunGitAsync(string repoPath, string gitArgs)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName               = "git",
        Arguments              = $"-C \"{repoPath}\" {gitArgs}",
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        CreateNoWindow         = true
    };
    using var proc = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to start git process.");
    var output = await proc.StandardOutput.ReadToEndAsync();
    await proc.WaitForExitAsync();
    return output;
}

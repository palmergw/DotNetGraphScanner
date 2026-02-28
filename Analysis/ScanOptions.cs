namespace DotNetGraphScanner.Analysis;

public sealed class ScanOptions
{
    /// <summary>Path to a .sln or .csproj file.</summary>
    public required string InputPath { get; init; }

    /// <summary>Output directory for generated files.</summary>
    public string OutputDir { get; init; } = ".";

    public bool DetectEntryPoints   { get; init; } = true;
    public bool AnalyzeCallGraph    { get; init; } = true;
    public bool AnalyzeDependencies { get; init; } = true;

    /// <summary>
    /// When false, external (non-project) method/type nodes are omitted from the
    /// HTML visualization (but kept in the JSON).
    /// </summary>
    public bool IncludeExternalNodes { get; init; } = false;
}

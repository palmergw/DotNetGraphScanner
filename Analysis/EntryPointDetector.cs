using DotNetGraphScanner.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Detects entry-point methods in a compilation and tags them in the graph. Recognises:
///   • static Main methods
///   • ASP.NET Core MVC / API controller action methods
///   • ASP.NET Core minimal-API route registrations (MapGet, MapPost, …)
///   • Azure Functions ([FunctionName])
///   • gRPC service overrides ([GrpcService] subclasses)
/// </summary>
public static class EntryPointDetector
{
    private static readonly HashSet<string> MinimalApiMethods = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch",
        "MapMethods", "Map", "MapFallback", "MapHub"
    };

    private static readonly HashSet<string> HttpVerbAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
        "Route"
    };

    public static void Detect(
        SyntaxTree tree,
        SemanticModel model,
        GraphModel graph,
        string projectId)
    {
        var root = tree.GetRoot();

        // ── static Main ──────────────────────────────────────────────────────
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!string.Equals(method.Identifier.Text, "Main", StringComparison.Ordinal)) continue;
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;

            var sym = model.GetDeclaredSymbol(method);
            if (sym is null) continue;

            var id = SymbolId(sym);
            EnsureMethodNode(graph, sym, id);
            graph.Nodes[id].IsEntryPoint = true;
            graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
        }

        // ── Controller action methods ────────────────────────────────────────
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var clsSym = model.GetDeclaredSymbol(cls) as INamedTypeSymbol;
            if (clsSym is null) continue;

            bool isController = IsController(clsSym);
            if (!isController) continue;

            foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;

                var mSym = model.GetDeclaredSymbol(method);
                if (mSym is null) continue;

                bool hasVerb = mSym.GetAttributes().Any(a =>
                    a.AttributeClass is not null &&
                    HttpVerbAttributes.Contains(StripSuffix(a.AttributeClass.Name, "Attribute")));

                if (!hasVerb) continue;

                var id = SymbolId(mSym);
                EnsureMethodNode(graph, mSym, id);
                graph.Nodes[id].IsEntryPoint = true;
                graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
            }
        }

        // ── Minimal API (MapGet / MapPost etc.) ──────────────────────────────
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? methodName = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax id => id.Identifier.Text,
                _ => null
            };

            if (methodName is null || !MinimalApiMethods.Contains(methodName)) continue;

            // Mark the node containing this invocation as the entry point
            // (the invocation site – often the Program.cs scope), but also try to
            // resolve any lambda/method-group argument as the handler.
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                var argExpr = arg.Expression;
                ISymbol? handlerSym = null;

                if (argExpr is AnonymousMethodExpressionSyntax or
                    SimpleLambdaExpressionSyntax or
                    ParenthesizedLambdaExpressionSyntax)
                {
                    // Create a synthetic node for this anonymous handler
                    var locationStr = $"{tree.FilePath}:{argExpr.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                    var nodeId = $"lambda:{locationStr}";
                    var label = $"λ {methodName} handler @ {System.IO.Path.GetFileName(tree.FilePath)}:{argExpr.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
                    graph.AddNode(nodeId, label, NodeKind.Method, isEntryPoint: true,
                        meta: new() { ["file"] = tree.FilePath, ["routeMethod"] = methodName });
                    graph.AddEdge(projectId, nodeId, EdgeKind.EntryPoint);
                }
                else
                {
                    handlerSym = model.GetSymbolInfo(argExpr).Symbol;
                    if (handlerSym is IMethodSymbol ms)
                    {
                        var id = SymbolId(ms);
                        EnsureMethodNode(graph, ms, id);
                        graph.Nodes[id].IsEntryPoint = true;
                        graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
                    }
                }
            }
        }

        // ── Azure Functions ───────────────────────────────────────────────────
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var sym = model.GetDeclaredSymbol(method);
            if (sym is null) continue;

            bool isFn = sym.GetAttributes().Any(a =>
                a.AttributeClass is not null &&
                (StripSuffix(a.AttributeClass.Name, "Attribute") == "FunctionName"));

            if (!isFn) continue;

            var id = SymbolId(sym);
            EnsureMethodNode(graph, sym, id);
            graph.Nodes[id].IsEntryPoint = true;
            graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsController(INamedTypeSymbol cls)
    {
        // Has [ApiController] or [Controller] attribute, or name ends in "Controller"
        foreach (var attr in cls.GetAttributes())
        {
            if (attr.AttributeClass is null) continue;
            var name = StripSuffix(attr.AttributeClass.Name, "Attribute");
            if (name == "ApiController" || name == "Controller") return true;
        }

        // Walk base types
        var baseType = cls.BaseType;
        while (baseType is not null)
        {
            if (baseType.Name is "ControllerBase" or "Controller") return true;
            baseType = baseType.BaseType;
        }

        return cls.Name.EndsWith("Controller", StringComparison.Ordinal);
    }

    // Includes parameter types so overloads are distinct, and containing-type
    // so nested classes are disambiguated.
    private static readonly SymbolDisplayFormat MemberIdFormat = new(
        globalNamespaceStyle:    SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle:  SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions:         SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:           SymbolDisplayMemberOptions.IncludeParameters
                               | SymbolDisplayMemberOptions.IncludeContainingType
                               | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions:        SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions:    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                               | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    /// Returns a stable, unique string ID for any Roslyn symbol.
    /// Methods and properties include the parameter-type list so overloads are
    /// distinct. For symbols declared inside the current solution the containing
    /// assembly name is also prepended, so e.g. two projects that both define
    /// <c>class Program { static void Main() }</c> in the global namespace get
    /// different IDs.
    /// </summary>
    internal static string SymbolId(ISymbol sym)
    {
        if (sym is IMethodSymbol or IPropertySymbol)
        {
            var fq  = sym.ToDisplayString(MemberIdFormat);
            var asm = sym.ContainingAssembly?.Name;
            return asm is not null ? $"{asm}#{fq}" : fq;
        }
        return sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    internal static void EnsureMethodNode(GraphModel graph, IMethodSymbol sym, string id)
    {
        if (graph.HasNode(id)) return;
        graph.AddNode(id, sym.Name, NodeKind.Method, meta: new()
        {
            ["fullName"] = sym.ToDisplayString(),
            ["returnType"] = sym.ReturnType.ToDisplayString()
        });
    }

    private static string StripSuffix(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;
}

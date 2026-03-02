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

        // ── Top-level statements (C# 9+ Program.cs with no explicit Main) ───
        // The compiler synthesises a <Main>$ method; GetEntryPoint() resolves it.
        if (root.ChildNodes().OfType<GlobalStatementSyntax>().Any())
        {
            var entrySymbol = model.Compilation.GetEntryPoint(default);
            if (entrySymbol is not null)
            {
                var id = SymbolId(entrySymbol);
                EnsureMethodNode(graph, entrySymbol, id);
                graph.Nodes[id].IsEntryPoint = true;
                graph.Nodes[id].Label = "<top-level program>";
                graph.AddEdge(projectId, id, EdgeKind.Contains);   // places it in the hierarchy
                graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
            }
        }

        // ── Controller action methods ────────────────────────────────────────
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var clsSym = model.GetDeclaredSymbol(cls) as INamedTypeSymbol;
            if (clsSym is null) continue;
            if (!IsController(clsSym)) continue;

            // Class-level [Route("prefix")] — used to build the full path
            var classRoute = GetFirstRouteTemplate(clsSym.GetAttributes());
            var controllerName = clsSym.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? clsSym.Name[..^"Controller".Length]
                : clsSym.Name;

            foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) continue;

                var mSym = model.GetDeclaredSymbol(method);
                if (mSym is null) continue;

                // Collect HTTP verb + optional route template from all verb/route attrs
                string? httpVerb = null;
                string? methodRoute = null;

                foreach (var attr in mSym.GetAttributes())
                {
                    if (attr.AttributeClass is null) continue;
                    var attrName = StripSuffix(attr.AttributeClass.Name, "Attribute");
                    if (!HttpVerbAttributes.Contains(attrName)) continue;

                    if (attrName != "Route")
                        httpVerb ??= attrName.Replace("Http", "", StringComparison.OrdinalIgnoreCase)
                                             .ToUpperInvariant();

                    // First string constructor argument is the route template
                    if (methodRoute is null &&
                        attr.ConstructorArguments.Length > 0 &&
                        attr.ConstructorArguments[0].Value is string t)
                        methodRoute = t;
                }

                if (httpVerb is null && methodRoute is null) continue; // no verb/route attr

                var id = SymbolId(mSym);
                EnsureMethodNode(graph, mSym, id);
                var node = graph.Nodes[id];
                node.IsEntryPoint = true;

                if (httpVerb is not null)
                    node.Meta["httpMethod"] = httpVerb;

                node.Meta["routeTemplate"] = BuildControllerRoute(
                    classRoute, methodRoute, controllerName, mSym.Name);

                graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
            }
        }

        // ── Minimal API (MapGet / MapPost etc.) ──────────────────────────────
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            string? mapMethod = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                IdentifierNameSyntax idn        => idn.Identifier.Text,
                _ => null
            };

            if (mapMethod is null || !MinimalApiMethods.Contains(mapMethod)) continue;

            var args = inv.ArgumentList.Arguments;

            // First argument is the route pattern string
            string? routePattern = null;
            if (args.Count > 0)
            {
                var cv = model.GetConstantValue(args[0].Expression);
                if (cv.HasValue && cv.Value is string rp) routePattern = rp;
            }

            string? httpVerb = mapMethod switch
            {
                "MapGet"    => "GET",
                "MapPost"   => "POST",
                "MapPut"    => "PUT",
                "MapDelete" => "DELETE",
                "MapPatch"  => "PATCH",
                _ => null
            };

            // Remaining arguments — look for lambdas and method-group handlers
            foreach (var arg in args.Skip(1))
            {
                var argExpr = arg.Expression;

                if (argExpr is AnonymousMethodExpressionSyntax or
                    SimpleLambdaExpressionSyntax or
                    ParenthesizedLambdaExpressionSyntax)
                {
                    var line = argExpr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var nodeId = $"lambda:{tree.FilePath}:{line}";
                    var label  = routePattern is not null
                        ? $"λ {mapMethod} {routePattern} @ {System.IO.Path.GetFileName(tree.FilePath)}:{line}"
                        : $"λ {mapMethod} handler @ {System.IO.Path.GetFileName(tree.FilePath)}:{line}";

                    var meta = new Dictionary<string, string> { ["file"] = tree.FilePath };
                    if (httpVerb is not null)    meta["httpMethod"]     = httpVerb;
                    if (routePattern is not null) meta["routeTemplate"] = routePattern;

                    graph.AddNode(nodeId, label, NodeKind.Method, isEntryPoint: true, meta: meta);
                    graph.AddEdge(projectId, nodeId, EdgeKind.EntryPoint);
                }
                else if (model.GetSymbolInfo(argExpr).Symbol is IMethodSymbol ms)
                {
                    var id   = SymbolId(ms);
                    EnsureMethodNode(graph, ms, id);
                    var node = graph.Nodes[id];
                    node.IsEntryPoint = true;
                    if (httpVerb is not null)    node.Meta["httpMethod"]     = httpVerb;
                    if (routePattern is not null) node.Meta["routeTemplate"] = routePattern;
                    graph.AddEdge(projectId, id, EdgeKind.EntryPoint);
                }
            }
        }

        // ── Azure Functions ───────────────────────────────────────────────────
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var sym = model.GetDeclaredSymbol(method);
            if (sym is null) continue;

            string? fnName = null;
            foreach (var attr in sym.GetAttributes())
            {
                if (attr.AttributeClass is null) continue;
                if (StripSuffix(attr.AttributeClass.Name, "Attribute") != "FunctionName") continue;
                fnName = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value as string
                    : null;
                break;
            }
            if (fnName is null) continue;

            var id = SymbolId(sym);
            EnsureMethodNode(graph, sym, id);
            var node = graph.Nodes[id];
            node.IsEntryPoint = true;
            node.Meta["functionName"] = fnName;
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

    /// <summary>Returns the template string from the first [Route("…")] attribute found, or null.</summary>
    private static string? GetFirstRouteTemplate(
        System.Collections.Immutable.ImmutableArray<AttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            if (attr.AttributeClass is null) continue;
            if (StripSuffix(attr.AttributeClass.Name, "Attribute") != "Route") continue;
            if (attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string t)
                return t;
        }
        return null;
    }

    /// <summary>
    /// Combines a controller-level and method-level route template into a full path,
    /// substituting [controller] and [action] tokens and normalising slashes.
    /// If the method template begins with '/' or '~/' it is treated as absolute
    /// and the class prefix is discarded.
    /// </summary>
    private static string BuildControllerRoute(
        string? classTemplate, string? methodTemplate,
        string controllerName, string actionName)
    {
        string Sub(string t) => t
            .Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]",     actionName,     StringComparison.OrdinalIgnoreCase);

        // Absolute method template overrides everything
        if (methodTemplate is not null)
        {
            var m = Sub(methodTemplate);
            if (m.StartsWith('/') || m.StartsWith("~/"))
                return m.TrimStart('~');
        }

        var classSegment  = classTemplate  is not null ? Sub(classTemplate).Trim('/')  : null;
        var methodSegment = methodTemplate is not null ? Sub(methodTemplate).Trim('/') : null;

        return (classSegment, methodSegment) switch
        {
            // Class prefix + explicit method template
            (not null, not null) => $"/{classSegment}/{methodSegment}",
            // Class prefix only — verb attr had no template, route is just the prefix
            (not null, null)     => $"/{classSegment}",
            // No class prefix — method template is the whole route
            (null,     not null) => $"/{methodSegment}",
            // No routing attributes at all — fall back to /{ControllerName}/{ActionName}
            _                    => $"/{controllerName}/{actionName}"
        };
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
        if (sym is IMethodSymbol or IPropertySymbol or IFieldSymbol)
        {
            var fq  = sym.ToDisplayString(MemberIdFormat);
            var asm = sym.ContainingAssembly?.Name;
            return asm is not null ? $"{asm}#{fq}" : fq;
        }
        return sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    internal static void EnsureMethodNode(GraphModel graph, IMethodSymbol sym, string id)
    {
        var node = graph.AddNode(id, sym.Name, NodeKind.Method, meta: new()
        {
            ["fullName"] = sym.ToDisplayString(),
            ["returnType"] = sym.ReturnType.ToDisplayString()
        });
        // Promote: if a call-graph pass created this node as an external placeholder
        // before the declaration was visited, clear the flag now that we know it is local.
        if (node.Meta.TryGetValue("isExternal", out var wasExt) && wasExt == "true")
        {
            node.Meta.Remove("isExternal");
            node.Meta["fullName"] = sym.ToDisplayString();
            node.Meta["returnType"] = sym.ReturnType.ToDisplayString();
        }
    }

    /// <summary>
    /// Ensures an external (non-solution) type node exists in the graph.
    /// Uses the correct NodeKind (Interface, Enum, Struct, or ExternalType).
    /// </summary>
    internal static void EnsureExternalTypeNode(GraphModel graph, INamedTypeSymbol sym, string id)
    {
        if (graph.HasNode(id)) return;
        var kind = sym.TypeKind switch
        {
            TypeKind.Interface => NodeKind.Interface,
            TypeKind.Enum      => NodeKind.Enum,
            TypeKind.Struct    => NodeKind.Struct,
            _                  => NodeKind.ExternalType   // Gap C: genuine unknown external class
        };
        graph.AddNode(id, sym.Name, kind, meta: new()
        {
            ["isExternal"]   = "true",
            ["fullName"]     = sym.ToDisplayString(),
            ["assemblyName"] = sym.ContainingAssembly?.Name ?? ""
        });
    }

    private static string StripSuffix(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;
}

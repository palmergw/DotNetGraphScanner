using DotNetGraphScanner.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Extracts type-level dependencies:
///   • class/struct inheritance
///   • interface implementations
///   • method parameter/return-type relationships
/// and registers namespaces and types as nodes in the graph.
/// </summary>
public static class DependencyAnalyzer
{
    public static void Analyze(SyntaxTree tree, SemanticModel model, GraphModel graph, string projectId)
    {
        var root = tree.GetRoot();

        // ── Named types ───────────────────────────────────────────────────────
        foreach (var typeDecl in root.DescendantNodes()
                     .OfType<BaseTypeDeclarationSyntax>())
        {
            var sym = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
            if (sym is null) continue;

            var typeId = EntryPointDetector.SymbolId(sym);
            var nodeKind = sym.TypeKind switch
            {
                TypeKind.Interface => NodeKind.Interface,
                TypeKind.Struct    => NodeKind.Struct,
                TypeKind.Enum      => NodeKind.Enum,
                _                  => NodeKind.Class
            };

            var typeNode = graph.AddNode(typeId, sym.Name, nodeKind, meta: new()
            {
                ["namespace"] = sym.ContainingNamespace?.ToDisplayString() ?? "",
                ["fullName"]  = sym.ToDisplayString()
            });
            // Promote: if this node was previously created as an external placeholder
            // (e.g. seen as an implemented interface before its own file was processed),
            // clear the flag and set the proper metadata now that we have the declaration.
            if (typeNode.Meta.TryGetValue("isExternal", out var wasExt) && wasExt == "true")
            {
                typeNode.Meta.Remove("isExternal");
                typeNode.Meta["namespace"] = sym.ContainingNamespace?.ToDisplayString() ?? "";
                typeNode.Meta["fullName"]  = sym.ToDisplayString();
                typeNode.Kind = nodeKind;
            }

            // Namespace node
            var ns = sym.ContainingNamespace;
            if (ns is not null && !ns.IsGlobalNamespace)
            {
                var nsId = "ns:" + ns.ToDisplayString();
                graph.AddNode(nsId, ns.ToDisplayString(), NodeKind.Namespace);
                graph.AddEdge(nsId, typeId, EdgeKind.Contains);
                graph.AddEdge(projectId, nsId, EdgeKind.Contains);
            }
            else
            {
                graph.AddEdge(projectId, typeId, EdgeKind.Contains);
            }

            // Inheritance
            if (sym.BaseType is not null &&
                sym.BaseType.SpecialType != SpecialType.System_Object)
            {
                var baseId = EntryPointDetector.SymbolId(sym.BaseType);
                EnsureExternalTypeNode(graph, sym.BaseType, baseId);
                graph.AddEdge(typeId, baseId, EdgeKind.Inherits);
            }

            // Interface implementations
            foreach (var iface in sym.Interfaces)
            {
                var ifaceId = EntryPointDetector.SymbolId(iface);
                EnsureExternalTypeNode(graph, iface, ifaceId);
                graph.AddEdge(typeId, ifaceId, EdgeKind.Implements);
            }

            // Register member methods (so call graph can find them later)
            foreach (var member in sym.GetMembers())
            {
                if (member is IMethodSymbol ms &&
                    ms.MethodKind is MethodKind.Ordinary or
                                     MethodKind.Constructor or
                                     MethodKind.UserDefinedOperator)
                {
                    var methodId = EntryPointDetector.SymbolId(ms);
                    EntryPointDetector.EnsureMethodNode(graph, ms, methodId);
                    graph.AddEdge(typeId, methodId, EdgeKind.Contains);
                }
                else if (member is IPropertySymbol ps)
                {
                    var propId  = EntryPointDetector.SymbolId(ps);
                    var propNode = graph.AddNode(propId, ps.Name, NodeKind.Property, meta: new()
                    {
                        ["fullName"] = ps.ToDisplayString(),
                        ["type"]     = ps.Type.ToDisplayString()
                    });
                    // Promote: CallGraphWalker may have pre-created this as an external placeholder.
                    if (propNode.Meta.TryGetValue("isExternal", out var propWasExt) && propWasExt == "true")
                    {
                        propNode.Meta.Remove("isExternal");
                        propNode.Meta["fullName"] = ps.ToDisplayString();
                        propNode.Meta["type"]     = ps.Type.ToDisplayString();
                    }
                    graph.AddEdge(typeId, propId, EdgeKind.Contains);
                }
            }
        }
    }

    private static void EnsureExternalTypeNode(GraphModel graph, INamedTypeSymbol sym, string id)
    {
        if (graph.HasNode(id)) return;
        graph.AddNode(id, sym.Name,
            sym.TypeKind == TypeKind.Interface ? NodeKind.Interface : NodeKind.Class,
            meta: new() { ["isExternal"] = "true", ["fullName"] = sym.ToDisplayString() });
    }
}

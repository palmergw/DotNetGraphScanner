using DotNetGraphScanner.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Walks all method bodies and records call-graph edges:
///   • Calls        – method and constructor invocations (Gap B)
///   • Accesses     – property reads, field reads, and enum-member accesses (Gap A)
///   • UsesAttribute – attributes decorating methods (Gap F)
/// Also handles anonymous function / lambda bodies as call contexts (Gap E).
/// Constructor and destructor bodies are also walked (Gap D).
/// </summary>
public sealed class CallGraphWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly GraphModel _graph;
    private readonly string _filePath;
    private IMethodSymbol? _currentMethod;
    private string? _currentLambdaId;   // Gap E: active lambda node-id when inside a tracked anonymous function

    /// <summary>
    /// The graph node-ID for the current call context.
    /// Inside a named method this is SymbolId(_currentMethod).
    /// Inside a tracked lambda this is _currentLambdaId.
    /// </summary>
    private string? CurrentCallerId =>
        _currentMethod is not null
            ? EntryPointDetector.SymbolId(_currentMethod)
            : _currentLambdaId;

    public CallGraphWalker(SemanticModel model, GraphModel graph, string filePath)
        : base(SyntaxWalkerDepth.Node)
    {
        _model = model;
        _graph = graph;
        _filePath = filePath;
    }

    // ── Visit methods ─────────────────────────────────────────────────────────

    public override void VisitCompilationUnit(CompilationUnitSyntax node)
    {
        // Top-level statements have no MethodDeclarationSyntax, so _currentMethod
        // is never set while walking them. Handle by resolving the synthetic entry
        // point and setting _currentMethod only while visiting GlobalStatementSyntax
        // nodes; class/struct/etc. members are walked normally.
        var hasGlobalStmts = node.Members.OfType<GlobalStatementSyntax>().Any();
        if (!hasGlobalStmts)
        {
            base.VisitCompilationUnit(node);
            return;
        }

        var entrySymbol = _model.Compilation.GetEntryPoint(default);
        foreach (var member in node.Members)
        {
            if (member is GlobalStatementSyntax && entrySymbol is not null)
            {
                var prev = _currentMethod;
                _currentMethod = entrySymbol;
                Visit(member);
                _currentMethod = prev;
            }
            else
            {
                Visit(member);
            }
        }
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var sym = _model.GetDeclaredSymbol(node);
        if (sym is not null)
        {
            var id = EntryPointDetector.SymbolId(sym);
            EntryPointDetector.EnsureMethodNode(_graph, sym, id);
            _currentMethod = sym;
            // Gap F: attribute usage on the method itself
            EmitAttributeEdges(id, sym.GetAttributes());
        }
        base.VisitMethodDeclaration(node);
        _currentMethod = null;
    }

    // Gap D: constructor bodies were previously never walked — calls inside ctors were dropped
    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var sym = _model.GetDeclaredSymbol(node);
        if (sym is not null)
        {
            var id = EntryPointDetector.SymbolId(sym);
            EntryPointDetector.EnsureMethodNode(_graph, sym, id);
            _currentMethod = sym;
            EmitAttributeEdges(id, sym.GetAttributes());
        }
        base.VisitConstructorDeclaration(node);
        _currentMethod = null;
    }

    // Gap D: destructor bodies
    public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        var sym = _model.GetDeclaredSymbol(node);
        if (sym is not null)
        {
            var id = EntryPointDetector.SymbolId(sym);
            EntryPointDetector.EnsureMethodNode(_graph, sym, id);
            _currentMethod = sym;
        }
        base.VisitDestructorDeclaration(node);
        _currentMethod = null;
    }

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var sym = _model.GetDeclaredSymbol(node);
        if (sym is not null)
        {
            var id = EntryPointDetector.SymbolId(sym);
            if (!_graph.HasNode(id))
            {
                _graph.AddNode(id, sym.Name, NodeKind.Method, meta: new()
                {
                    ["fullName"] = sym.ToDisplayString(),
                    ["isLocalFunction"] = "true",
                    ["returnType"] = sym.ReturnType.ToDisplayString()
                });
            }
            var prev = _currentMethod;
            _currentMethod = sym;
            base.VisitLocalFunctionStatement(node);
            _currentMethod = prev;
            return;
        }
        base.VisitLocalFunctionStatement(node);
    }

    // Gap E: anonymous / lambda visitors ─────────────────────────────────────

    public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        => VisitLambdaCore(node, () => base.VisitAnonymousMethodExpression(node));

    public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        => VisitLambdaCore(node, () => base.VisitSimpleLambdaExpression(node));

    public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        => VisitLambdaCore(node, () => base.VisitParenthesizedLambdaExpression(node));

    /// <summary>
    /// If this lambda was registered as an entry-point node by EntryPointDetector
    /// (e.g. a MapGet/MapPost handler), switch the call context to that node so that
    /// calls inside the lambda are attributed to the entry-point rather than dropped.
    /// Otherwise the lambda body is walked normally, attributing calls to any enclosing method.
    /// </summary>
    private void VisitLambdaCore(SyntaxNode lambdaNode, Action visitBase)
    {
        var line     = lambdaNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var lambdaId = $"lambda:{_filePath}:{line}";

        if (_graph.HasNode(lambdaId))
        {
            var prevMethod   = _currentMethod;
            var prevLambdaId = _currentLambdaId;
            _currentMethod   = null;
            _currentLambdaId = lambdaId;
            visitBase();
            _currentMethod   = prevMethod;
            _currentLambdaId = prevLambdaId;
        }
        else
        {
            visitBase();  // calls still attributed to enclosing method if any
        }
    }

    // Gap A: member-access — properties, fields, enum members ─────────────────

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var callerId = CurrentCallerId;
        if (callerId is not null)
        {
            var info = _model.GetSymbolInfo(node);
            var sym  = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

            if (sym is IPropertySymbol ps)
            {
                EnsureCallerIfMethod(callerId);
                var propId = EntryPointDetector.SymbolId(ps);
                if (!_graph.HasNode(propId))
                {
                    _graph.AddNode(propId, ps.Name, NodeKind.Property, meta: new()
                    {
                        ["fullName"]   = ps.ToDisplayString(),
                        ["isExternal"] = "true",
                        ["type"]       = ps.Type.ToDisplayString()
                    });
                }
                _graph.AddEdge(callerId, propId, EdgeKind.Accesses);
            }
            else if (sym is IFieldSymbol fs)
            {
                EnsureCallerIfMethod(callerId);
                if (fs.ContainingType?.TypeKind == TypeKind.Enum)
                {
                    // Enum member access → Accesses edge points at the enum type node
                    var enumId = EntryPointDetector.SymbolId(fs.ContainingType);
                    EntryPointDetector.EnsureExternalTypeNode(_graph, fs.ContainingType, enumId);
                    _graph.AddEdge(callerId, enumId, EdgeKind.Accesses);
                }
                else
                {
                    // Regular field
                    var fieldId = EntryPointDetector.SymbolId(fs);
                    if (!_graph.HasNode(fieldId))
                    {
                        _graph.AddNode(fieldId, fs.Name, NodeKind.Field, meta: new()
                        {
                            ["fullName"]   = fs.ToDisplayString(),
                            ["isExternal"] = fs.DeclaringSyntaxReferences.IsEmpty ? "true" : "false",
                            ["type"]       = fs.Type.ToDisplayString()
                        });
                    }
                    _graph.AddEdge(callerId, fieldId, EdgeKind.Accesses);
                }
            }
        }
        base.VisitMemberAccessExpression(node);
    }

    // ── Method invocations ────────────────────────────────────────────────────

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var callerId = CurrentCallerId;
        if (callerId is not null)
        {
            var calleeInfo = _model.GetSymbolInfo(node);
            var calleeSym  = (calleeInfo.Symbol ?? calleeInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;

            if (calleeSym is not null)
            {
                var calleeId = EntryPointDetector.SymbolId(calleeSym);
                EnsureCallerIfMethod(callerId);

                // External symbol (different assembly) – create a lightweight node
                if (!_graph.HasNode(calleeId))
                {
                    _graph.AddNode(calleeId, calleeSym.Name, NodeKind.Method, meta: new()
                    {
                        ["fullName"]   = calleeSym.ToDisplayString(),
                        ["isExternal"] = "true",
                        ["returnType"] = calleeSym.ReturnType.ToDisplayString()
                    });
                }

                var lineNum = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                _graph.AddEdge(callerId, calleeId, EdgeKind.Calls, meta: new()
                {
                    ["file"] = _filePath,
                    ["line"] = lineNum.ToString()
                });
            }
        }
        base.VisitInvocationExpression(node);
    }

    // Gap B: object creation (new Foo() / new()) ──────────────────────────────

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        EmitConstructorCall(node);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        EmitConstructorCall(node);
        base.VisitImplicitObjectCreationExpression(node);
    }

    private void EmitConstructorCall(ExpressionSyntax node)
    {
        var callerId = CurrentCallerId;
        if (callerId is null) return;

        var ctorInfo = _model.GetSymbolInfo(node);
        if ((ctorInfo.Symbol ?? ctorInfo.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol ctorSym) return;

        var ctorId = EntryPointDetector.SymbolId(ctorSym);
        EnsureCallerIfMethod(callerId);

        if (!_graph.HasNode(ctorId))
        {
            _graph.AddNode(ctorId, ctorSym.Name, NodeKind.Method, meta: new()
            {
                ["fullName"]   = ctorSym.ToDisplayString(),
                ["isExternal"] = "true",
                ["returnType"] = ctorSym.ReturnType.ToDisplayString()
            });
        }

        var lineNum = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _graph.AddEdge(callerId, ctorId, EdgeKind.Calls, meta: new()
        {
            ["file"] = _filePath,
            ["line"] = lineNum.ToString()
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the caller node exists in the graph.
    /// Named-method callers are created lazily via EnsureMethodNode.
    /// Lambda callers are pre-created by EntryPointDetector — no action needed.
    /// </summary>
    private void EnsureCallerIfMethod(string callerId)
    {
        if (_currentMethod is not null)
            EntryPointDetector.EnsureMethodNode(_graph, _currentMethod, callerId);
    }

    /// <summary>Gap F: emit UsesAttribute edges from a node to each attribute's type node.</summary>
    private void EmitAttributeEdges(string nodeId,
        System.Collections.Immutable.ImmutableArray<AttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            if (attr.AttributeClass is null) continue;
            var attrId = EntryPointDetector.SymbolId(attr.AttributeClass);
            EntryPointDetector.EnsureExternalTypeNode(_graph, attr.AttributeClass, attrId);
            _graph.AddEdge(nodeId, attrId, EdgeKind.UsesAttribute);
        }
    }
}

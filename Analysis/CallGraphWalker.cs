using DotNetGraphScanner.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotNetGraphScanner.Analysis;

/// <summary>
/// Walks all method bodies and records CALLS edges between resolved method symbols.
/// </summary>
public sealed class CallGraphWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _model;
    private readonly GraphModel _graph;
    private readonly string _filePath;
    private IMethodSymbol? _currentMethod;

    public CallGraphWalker(SemanticModel model, GraphModel graph, string filePath)
        : base(SyntaxWalkerDepth.Node)
    {
        _model = model;
        _graph = graph;
        _filePath = filePath;
    }

    // ── Visit methods ─────────────────────────────────────────────────────────

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var sym = _model.GetDeclaredSymbol(node);
        if (sym is not null)
        {
            var id = EntryPointDetector.SymbolId(sym);
            EntryPointDetector.EnsureMethodNode(_graph, sym, id);
            _currentMethod = sym;
        }
        base.VisitMethodDeclaration(node);
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

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (_currentMethod is not null)
        {
            var callerSym = _currentMethod;
            var calleeInfo = _model.GetSymbolInfo(node);
            var calleeSym = (calleeInfo.Symbol ?? calleeInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;

            if (calleeSym is not null)
            {
                var callerId = EntryPointDetector.SymbolId(callerSym);
                var calleeId = EntryPointDetector.SymbolId(calleeSym);

                EntryPointDetector.EnsureMethodNode(_graph, callerSym, callerId);

                // External symbol (different assembly) – create a lightweight node
                if (!_graph.HasNode(calleeId))
                {
                    _graph.AddNode(calleeId, calleeSym.Name, NodeKind.Method, meta: new()
                    {
                        ["fullName"] = calleeSym.ToDisplayString(),
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
}

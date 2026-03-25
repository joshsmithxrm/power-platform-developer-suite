using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS007: Detects pool clients held across multiple await calls.
/// Get a fresh client for each operation to maximize pool parallelism.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PoolClientInParallelAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.PoolClientInParallel,
        title: "Pool client used across multiple await calls",
        messageFormat: "Pool client '{0}' is used across multiple await calls; get a fresh client for each operation",
        category: DiagnosticCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Holding a pooled IPooledClient across multiple sequential await calls " +
                     "blocks a pool slot during idle time between operations, defeating parallelism. " +
                     "Get a fresh client for each operation. See Constitution D2.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        var localDecl = (LocalDeclarationStatementSyntax)context.Node;

        // We need exactly one variable declarator
        if (localDecl.Declaration.Variables.Count != 1)
            return;

        var declarator = localDecl.Declaration.Variables[0];

        // Check if the initializer calls GetClientAsync or GetClient
        if (!IsPoolClientAcquisition(declarator, context.SemanticModel, context.CancellationToken))
            return;

        var variableName = declarator.Identifier.Text;

        // Resolve the declared symbol for semantic comparison
        var declaredSymbol = context.SemanticModel.GetDeclaredSymbol(declarator, context.CancellationToken);

        // Find the enclosing block
        var enclosingBlock = localDecl.Parent;
        if (enclosingBlock is null)
            return;

        // Count await expressions where this variable is the receiver
        var awaitCount = CountAwaitsOnVariable(
            enclosingBlock, variableName, declaredSymbol, context.SemanticModel,
            localDecl.SpanStart, context.CancellationToken);

        if (awaitCount > 1)
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                declarator.GetLocation(),
                variableName);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool IsPoolClientAcquisition(
        VariableDeclaratorSyntax declarator,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        if (declarator.Initializer?.Value is null)
            return false;

        var initValue = declarator.Initializer.Value;

        // Unwrap await: await _pool.GetClientAsync(...)
        if (initValue is AwaitExpressionSyntax awaitExpr)
            initValue = awaitExpr.Expression;

        // Check for invocation: _pool.GetClientAsync(...)
        if (initValue is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;
                if (methodName is "GetClientAsync" or "GetClient")
                    return true;
            }
        }

        // Also check the resolved type of the variable
        var symbol = semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
        if (symbol is ILocalSymbol localSymbol)
        {
            var typeName = localSymbol.Type.Name;
            if (typeName == "IPooledClient")
                return true;

            // Check interfaces
            foreach (var iface in localSymbol.Type.AllInterfaces)
            {
                if (iface.Name == "IPooledClient")
                    return true;
            }
        }

        return false;
    }

    private static int CountAwaitsOnVariable(
        SyntaxNode scope,
        string variableName,
        ISymbol? declaredSymbol,
        SemanticModel semanticModel,
        int afterPosition,
        System.Threading.CancellationToken cancellationToken)
    {
        var count = 0;

        foreach (var awaitExpr in scope.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            // Only count awaits after the declaration
            if (awaitExpr.SpanStart <= afterPosition)
                continue;

            // Skip awaits inside nested lambdas/local functions (different execution scope)
            if (IsInsideNestedScope(awaitExpr, scope))
                continue;

            // Check if the awaited invocation uses our variable as receiver
            var expression = awaitExpr.Expression;

            // Unwrap ConfigureAwait: client.X().ConfigureAwait(false)
            if (expression is InvocationExpressionSyntax outerInvocation &&
                outerInvocation.Expression is MemberAccessExpressionSyntax outerAccess &&
                outerAccess.Name.Identifier.Text == "ConfigureAwait")
            {
                expression = outerAccess.Expression;
            }

            if (expression is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Expression is not IdentifierNameSyntax identifier)
                    continue;

                // Prefer semantic comparison when available, fall back to name
                if (declaredSymbol is not null)
                {
                    var referencedSymbol = semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(referencedSymbol, declaredSymbol))
                        count++;
                }
                else if (identifier.Identifier.Text == variableName)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsInsideNestedScope(SyntaxNode node, SyntaxNode boundary)
    {
        var current = node.Parent;
        while (current is not null && current != boundary)
        {
            if (current is LambdaExpressionSyntax or
                AnonymousMethodExpressionSyntax or
                LocalFunctionStatementSyntax)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }
}

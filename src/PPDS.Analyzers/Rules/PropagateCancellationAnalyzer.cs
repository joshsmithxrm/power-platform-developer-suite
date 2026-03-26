using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS011: Detects async methods with CancellationToken that don't pass it downstream.
/// CancellationToken must be threaded through the entire async call chain.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PropagateCancellationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.PropagateCancellation,
        title: "Propagate CancellationToken to async calls",
        messageFormat: "Async call '{0}' does not pass the CancellationToken parameter '{1}'",
        category: DiagnosticCategories.Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Async methods that accept a CancellationToken must pass it to all downstream " +
                     "async calls that support cancellation. Dropping the token makes the operation " +
                     "uncancellable. See Constitution R2.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunction, SyntaxKind.LocalFunctionStatement);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        AnalyzeMethodLike(context, method.ParameterList, (SyntaxNode?)method.Body ?? method.ExpressionBody);
    }

    private static void AnalyzeLocalFunction(SyntaxNodeAnalysisContext context)
    {
        var localFunc = (LocalFunctionStatementSyntax)context.Node;
        AnalyzeMethodLike(context, localFunc.ParameterList, (SyntaxNode?)localFunc.Body ?? localFunc.ExpressionBody);
    }

    private static void AnalyzeMethodLike(
        SyntaxNodeAnalysisContext context,
        ParameterListSyntax? parameterList,
        SyntaxNode? body)
    {
        if (parameterList is null || body is null)
            return;

        // Find the CancellationToken parameter
        var ctParamName = FindCancellationTokenParam(parameterList, context.SemanticModel, context.CancellationToken);
        if (ctParamName is null)
            return;

        // Check awaited invocations in the method body.
        // Non-awaited async calls (fire-and-forget, task variables) are intentionally excluded
        // to avoid false positives on patterns like Task.WhenAll or stored tasks.
        foreach (var awaitExpr in body.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            // Skip awaits inside nested lambdas/local functions (they have their own scope)
            if (IsInsideNestedScope(awaitExpr, body))
                continue;

            var invocation = UnwrapAwaitToInvocation(awaitExpr);
            if (invocation is null)
                continue;

            // Check if the invocation already passes a CancellationToken
            if (PassesCancellationToken(invocation, context.SemanticModel, context.CancellationToken))
                continue;

            // Check if the called method has an overload that accepts CancellationToken
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
            var calledMethod = symbolInfo.Symbol as IMethodSymbol;
            if (calledMethod is null)
                continue;

            if (!HasCancellationTokenOverload(calledMethod))
                continue;

            var methodName = calledMethod.Name;
            var diagnostic = Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                methodName,
                ctParamName);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static string? FindCancellationTokenParam(
        ParameterListSyntax parameterList,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        foreach (var param in parameterList.Parameters)
        {
            var paramSymbol = semanticModel.GetDeclaredSymbol(param, cancellationToken);
            if (paramSymbol?.Type is not null &&
                paramSymbol.Type.Name == "CancellationToken" &&
                paramSymbol.Type.ContainingNamespace?.ToDisplayString() == "System.Threading")
            {
                return param.Identifier.Text;
            }
        }

        return null;
    }

    private static bool IsInsideNestedScope(SyntaxNode node, SyntaxNode methodBody)
    {
        var current = node.Parent;
        while (current is not null && current != methodBody)
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

    private static InvocationExpressionSyntax? UnwrapAwaitToInvocation(AwaitExpressionSyntax awaitExpr)
    {
        var expression = awaitExpr.Expression;

        // Unwrap .ConfigureAwait(false)
        if (expression is InvocationExpressionSyntax configureAwait &&
            configureAwait.Expression is MemberAccessExpressionSyntax configAccess &&
            configAccess.Name.Identifier.Text == "ConfigureAwait")
        {
            expression = configAccess.Expression;
        }

        return expression as InvocationExpressionSyntax;
    }

    private static bool PassesCancellationToken(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        System.Threading.CancellationToken cancellationToken)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var typeInfo = semanticModel.GetTypeInfo(arg.Expression, cancellationToken);
            if (typeInfo.Type is not null &&
                typeInfo.Type.Name == "CancellationToken" &&
                typeInfo.Type.ContainingNamespace?.ToDisplayString() == "System.Threading")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCancellationTokenOverload(IMethodSymbol calledMethod)
    {
        // Check if the method itself already has a CancellationToken parameter
        // (meaning it was called without passing it)
        foreach (var param in calledMethod.Parameters)
        {
            if (param.Type.Name == "CancellationToken" &&
                param.Type.ContainingNamespace?.ToDisplayString() == "System.Threading")
            {
                // The method accepts CT but we're not passing it — always flag
                return true;
            }
        }

        // Check sibling overloads on the same type
        var containingType = calledMethod.ContainingType;
        if (containingType is null)
            return false;

        foreach (var member in containingType.GetMembers(calledMethod.Name))
        {
            if (member is not IMethodSymbol sibling || SymbolEqualityComparer.Default.Equals(sibling, calledMethod))
                continue;

            // Check if this sibling has a CancellationToken parameter
            foreach (var param in sibling.Parameters)
            {
                if (param.Type.Name == "CancellationToken" &&
                    param.Type.ContainingNamespace?.ToDisplayString() == "System.Threading")
                {
                    return true;
                }
            }
        }

        return false;
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS013: Detects async method calls in constructors without await.
/// Fire-and-forget in constructors causes race conditions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoFireAndForgetInCtorAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.NoFireAndForgetInCtor,
        title: "Avoid fire-and-forget async in constructors",
        messageFormat: "Async method '{0}' called in constructor without await; use Loaded event for async initialization",
        category: DiagnosticCategories.Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Calling async methods in constructors without await causes race conditions. " +
                     "The async operation may complete before the UI is ready. " +
                     "Use Loaded events or factory methods for async initialization. " +
                     "See Gemini PR#242 feedback.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeConstructor, SyntaxKind.ConstructorDeclaration);
    }

    private static void AnalyzeConstructor(SyntaxNodeAnalysisContext context)
    {
        var constructor = (ConstructorDeclarationSyntax)context.Node;

        if (constructor.Body is null && constructor.ExpressionBody is null)
            return;

        // Find all invocation expressions in the constructor
        var invocations = constructor.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            // Skip if this invocation is already awaited
            if (IsAwaited(invocation))
                continue;

            // Check if the method returns a Task
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
            if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                continue;

            if (!ReturnsTask(methodSymbol))
                continue;

            // Skip common non-async patterns that return Task
            if (IsCommonSafePattern(methodSymbol))
                continue;

            // Skip if the task has .ContinueWith() error handling attached
            if (HasContinueWithErrorHandling(invocation))
                continue;

            var methodName = GetMethodName(invocation);

            var diagnostic = Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                methodName);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool IsAwaited(InvocationExpressionSyntax invocation)
    {
        // Check if parent is an await expression
        var parent = invocation.Parent;

        // Handle parenthesized expressions
        while (parent is ParenthesizedExpressionSyntax)
        {
            parent = parent.Parent;
        }

        return parent is AwaitExpressionSyntax;
    }

    private static bool ReturnsTask(IMethodSymbol method)
    {
        var returnType = method.ReturnType;

        if (returnType is null)
            return false;

        var typeName = returnType.Name;
        var containingNamespace = returnType.ContainingNamespace?.ToDisplayString();

        // Check for Task, Task<T>, ValueTask, ValueTask<T>
        if (containingNamespace == "System.Threading.Tasks")
        {
            return typeName is "Task" or "ValueTask";
        }

        // Check for generic versions
        if (returnType is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            var originalDef = namedType.OriginalDefinition;
            var originalName = originalDef.Name;
            var originalNamespace = originalDef.ContainingNamespace?.ToDisplayString();

            if (originalNamespace == "System.Threading.Tasks")
            {
                return originalName is "Task" or "ValueTask";
            }
        }

        return false;
    }

    private static bool IsCommonSafePattern(IMethodSymbol method)
    {
        // Some Task-returning methods are commonly used in fire-and-forget scenarios
        // and are generally safe (e.g., Task.Run for background work that's properly handled)
        var methodName = method.Name;
        var containingType = method.ContainingType?.Name;

        // Task.CompletedTask, Task.FromResult are safe
        if (containingType == "Task" && methodName is "FromResult" or "FromException" or "FromCanceled")
            return true;

        return false;
    }

    private static bool HasContinueWithErrorHandling(InvocationExpressionSyntax invocation)
    {
        // Check if this invocation is part of a .ContinueWith() chain
        // Pattern: _ = SomeAsync().ContinueWith(...)
        var parent = invocation.Parent;

        // The invocation might be the expression in a member access like SomeAsync().ContinueWith
        if (parent is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name.Identifier.Text == "ContinueWith")
        {
            return true;
        }

        // Also check if the entire statement is a discard assignment with ContinueWith
        // Pattern: _ = SomeAsync().ContinueWith(t => { ... });
        // In this case, the invocation is SomeAsync(), and we need to check if it's
        // the object of a .ContinueWith() call
        if (parent is MemberAccessExpressionSyntax outerMemberAccess)
        {
            var grandparent = outerMemberAccess.Parent;
            if (grandparent is InvocationExpressionSyntax &&
                outerMemberAccess.Name.Identifier.Text == "ContinueWith")
            {
                return true;
            }
        }

        return false;
    }

    private static string GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => invocation.Expression.ToString()
        };
    }
}

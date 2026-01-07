using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS012: Detects sync-over-async patterns that can cause deadlocks.
/// Flags .GetAwaiter().GetResult(), .Result, and .Wait() on tasks.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoSyncOverAsyncAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.NoSyncOverAsync,
        title: "Avoid sync-over-async patterns",
        messageFormat: "'{0}' can cause deadlocks; use 'await' instead",
        category: DiagnosticCategories.Correctness,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Calling .GetAwaiter().GetResult(), .Result, or .Wait() on tasks can cause deadlocks. " +
                     "Use async/await patterns instead. See Gemini PR#242 feedback.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        // Check for .Result property access
        if (memberAccess.Name.Identifier.Text != "Result")
            return;

        // Verify it's on a Task type
        var typeInfo = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken);
        if (!IsTaskType(typeInfo.Type))
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            memberAccess.Name.GetLocation(),
            ".Result");

        context.ReportDiagnostic(diagnostic);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        var methodName = memberAccess.Name.Identifier.Text;

        // Check for .Wait() call
        if (methodName == "Wait")
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken);
            if (IsTaskType(typeInfo.Type))
            {
                var diagnostic = Diagnostic.Create(
                    Rule,
                    memberAccess.Name.GetLocation(),
                    ".Wait()");

                context.ReportDiagnostic(diagnostic);
                return;
            }
        }

        // Check for .GetResult() call (from .GetAwaiter().GetResult())
        if (methodName == "GetResult" &&
            memberAccess.Expression is InvocationExpressionSyntax innerInvocation &&
            innerInvocation.Expression is MemberAccessExpressionSyntax innerMemberAccess &&
            innerMemberAccess.Name.Identifier.Text == "GetAwaiter")
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(innerMemberAccess.Expression, context.CancellationToken);
            if (IsTaskType(typeInfo.Type))
            {
                var diagnostic = Diagnostic.Create(
                    Rule,
                    invocation.GetLocation(),
                    ".GetAwaiter().GetResult()");

                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static bool IsTaskType(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        // Check for Task, Task<T>, ValueTask, ValueTask<T>
        var typeName = type.Name;
        var containingNamespace = type.ContainingNamespace?.ToDisplayString();

        if (containingNamespace == "System.Threading.Tasks")
        {
            return typeName is "Task" or "ValueTask";
        }

        // Also check for generic versions
        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
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
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS010: Detects QueryExpression passed to RetrieveMultiple without TopCount being set.
/// Setting TopCount prevents unbounded result sets that can degrade performance.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ValidateTopCountAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.ValidateTopCount,
        title: "Set TopCount on QueryExpression to prevent unbounded result sets",
        messageFormat: "Set TopCount on QueryExpression to prevent unbounded result sets",
        category: DiagnosticCategories.Performance,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "QueryExpression objects passed to RetrieveMultiple should have TopCount set " +
                     "to prevent unbounded result sets that can degrade performance.");

    private static readonly string[] RetrieveMultipleMethods =
    {
        "RetrieveMultiple",
        "RetrieveMultipleAsync",
    };

    private static readonly string[] ServiceInterfaces =
    {
        "IOrganizationService",
        "IOrganizationServiceAsync",
        "IOrganizationServiceAsync2",
        "IDataverseClient",
        "IPooledClient",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        var methodName = memberAccess.Name.Identifier.Text;

        if (Array.IndexOf(RetrieveMultipleMethods, methodName) < 0)
            return;

        // Verify the method belongs to a known service type
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return;

        if (!IsServiceInterface(methodSymbol.ContainingType))
            return;

        // Get the first argument
        if (invocation.ArgumentList.Arguments.Count == 0)
            return;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;

        // Check the type of the argument — only flag QueryExpression, not FetchExpression etc.
        var argTypeInfo = context.SemanticModel.GetTypeInfo(firstArg, context.CancellationToken);
        if (argTypeInfo.Type is null || argTypeInfo.Type.Name != "QueryExpression")
            return;

        // Check if TopCount is set
        if (HasTopCountAssigned(firstArg, context, invocation))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool HasTopCountAssigned(
        ExpressionSyntax argument,
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax callSite)
    {
        // Case 1: Inline object creation with initializer — new QueryExpression("x") { TopCount = 10 }
        if (argument is ObjectCreationExpressionSyntax objectCreation)
        {
            if (objectCreation.Initializer is not null)
            {
                foreach (var expression in objectCreation.Initializer.Expressions)
                {
                    if (expression is AssignmentExpressionSyntax assignment &&
                        assignment.Left is IdentifierNameSyntax identifier &&
                        identifier.Identifier.Text == "TopCount")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Case 2: Variable passed as argument — check for variable.TopCount = ... BEFORE the call
        if (argument is not IdentifierNameSyntax variableName)
            return true; // Non-local expressions (method returns, property access, ternary, etc.) — skip to avoid false positives

        var varName = variableName.Identifier.Text;
        var callPosition = callSite.SpanStart;

        // Find the enclosing block
        var enclosingBlock = argument.FirstAncestorOrSelf<BlockSyntax>();
        if (enclosingBlock is null)
            return false;

        // Search assignment expressions BEFORE the call site for varName.TopCount = ...
        foreach (var assignment in enclosingBlock.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.SpanStart >= callPosition)
                continue; // Assignment is after the call — doesn't count

            if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "TopCount" &&
                memberAccess.Expression is IdentifierNameSyntax target &&
                target.Identifier.Text == varName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsServiceInterface(INamedTypeSymbol? type)
    {
        if (type is null)
            return false;

        if (MatchesKnownType(type))
            return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (MatchesKnownType(iface))
                return true;
        }

        return false;
    }

    private static bool MatchesKnownType(INamedTypeSymbol type)
    {
        if (Array.IndexOf(ServiceInterfaces, type.Name) < 0)
            return false;

        // Verify the namespace to avoid false positives on unrelated types with the same name
        var ns = type.ContainingNamespace?.ToDisplayString();
        return ns is "Microsoft.Xrm.Sdk" or "Microsoft.PowerPlatform.Dataverse.Client" or "PPDS.Dataverse";
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS009: Detects RetrieveMultiple results used only for counting
/// (e.g., svc.RetrieveMultiple(qe).Entities.Count or svc.RetrieveMultiple(qe).TotalRecordCount).
/// Use FetchXML aggregate queries instead for efficient server-side counting.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseAggregateForCountAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.UseAggregateForCount,
        title: "Use FetchXML aggregate query for counting instead of RetrieveMultiple",
        messageFormat: "Use FetchXML aggregate query for counting instead of RetrieveMultiple().Entities.Count",
        category: DiagnosticCategories.Performance,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Calling RetrieveMultiple and accessing .Entities.Count or .TotalRecordCount " +
                     "retrieves all records just to count them. Use a FetchXML aggregate query instead.");

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

        // Check if the invocation result is immediately chained with a counting access pattern:
        // - .Entities.Count  (RetrieveMultiple(...).Entities.Count)
        // - .TotalRecordCount (RetrieveMultiple(...).TotalRecordCount)
        // - .TotalRecordCountLimitExceeded (RetrieveMultiple(...).TotalRecordCountLimitExceeded)
        if (IsCountingAccess(invocation))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }

    private static bool IsCountingAccess(InvocationExpressionSyntax invocation)
    {
        // Walk up the parent chain to see if the result is accessed for counting.
        // Pattern 1: svc.RetrieveMultiple(qe).Entities.Count
        //   invocation -> MemberAccess(.Entities) -> MemberAccess(.Count)
        // Pattern 2: svc.RetrieveMultiple(qe).TotalRecordCount
        //   invocation -> MemberAccess(.TotalRecordCount)

        if (invocation.Parent is not MemberAccessExpressionSyntax firstAccess)
            return false;

        var firstName = firstAccess.Name.Identifier.Text;

        // Pattern 2: .TotalRecordCount or .TotalRecordCountLimitExceeded directly
        if (firstName is "TotalRecordCount" or "TotalRecordCountLimitExceeded")
            return true;

        // Pattern 1: .Entities.Count
        if (firstName == "Entities" &&
            firstAccess.Parent is MemberAccessExpressionSyntax secondAccess)
        {
            var secondName = secondAccess.Name.Identifier.Text;
            if (secondName == "Count")
                return true;
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
        return Array.IndexOf(ServiceInterfaces, type.Name) >= 0;
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS008: Detects individual CRUD calls inside loops.
/// Use bulk operations (CreateMultipleAsync, etc.) for 5x better performance.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseBulkOperationsAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.UseBulkOperations,
        title: "Use bulk operations instead of individual calls in loops",
        messageFormat: "Loop calls '{0}' individually; use '{1}' for 5x better performance",
        category: DiagnosticCategories.Performance,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Individual Create/Update/Delete calls inside loops should be replaced " +
                     "with CreateMultipleAsync/UpdateMultipleAsync/DeleteMultipleAsync from " +
                     "IBulkOperationExecutor for 5x better performance. See Constitution D3.");

    private static readonly IReadOnlyDictionary<string, string> MethodToBulk =
        new Dictionary<string, string>
        {
            ["Create"] = "CreateMultipleAsync",
            ["CreateAsync"] = "CreateMultipleAsync",
            ["Update"] = "UpdateMultipleAsync",
            ["UpdateAsync"] = "UpdateMultipleAsync",
            ["Delete"] = "DeleteMultipleAsync",
            ["DeleteAsync"] = "DeleteMultipleAsync",
            ["Execute"] = "ExecuteMultiple",
            ["ExecuteAsync"] = "ExecuteMultiple",
        };

    // Fully qualified interface names that declare CRUD operations
    private static readonly string[] CrudInterfaces =
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
        context.RegisterSyntaxNodeAction(AnalyzeLoop, SyntaxKind.ForEachStatement);
        context.RegisterSyntaxNodeAction(AnalyzeLoop, SyntaxKind.ForStatement);
        context.RegisterSyntaxNodeAction(AnalyzeLoop, SyntaxKind.WhileStatement);
        context.RegisterSyntaxNodeAction(AnalyzeLoop, SyntaxKind.DoStatement);
    }

    private static void AnalyzeLoop(SyntaxNodeAnalysisContext context)
    {
        var loopBody = GetLoopBody(context.Node);
        if (loopBody is null)
            return;

        foreach (var invocation in loopBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var methodName = memberAccess.Name.Identifier.Text;

            if (!MethodToBulk.TryGetValue(methodName, out var bulkAlternative))
                continue;

            // Verify the method belongs to a Dataverse client type
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
            if (methodSymbol is null)
                continue;

            if (!IsCrudInterface(methodSymbol.ContainingType))
                continue;

            var diagnostic = Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                methodName,
                bulkAlternative);

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static SyntaxNode? GetLoopBody(SyntaxNode node) => node switch
    {
        ForEachStatementSyntax forEach => forEach.Statement,
        ForStatementSyntax forStmt => forStmt.Statement,
        WhileStatementSyntax whileStmt => whileStmt.Statement,
        DoStatementSyntax doStmt => doStmt.Statement,
        _ => null,
    };

    private static bool IsCrudInterface(INamedTypeSymbol? type)
    {
        if (type is null)
            return false;

        // Check the type itself and all its interfaces
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
        if (Array.IndexOf(CrudInterfaces, type.Name) < 0)
            return false;

        // Verify the namespace to avoid false positives on unrelated types with the same name
        var ns = type.ContainingNamespace?.ToDisplayString();
        return ns is "Microsoft.Xrm.Sdk" or "Microsoft.PowerPlatform.Dataverse.Client" or "PPDS.Dataverse";
    }
}

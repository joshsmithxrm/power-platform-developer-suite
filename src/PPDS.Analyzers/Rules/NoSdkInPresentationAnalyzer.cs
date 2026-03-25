using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS005: Detects direct usage of Dataverse SDK types (ServiceClient, IOrganizationService,
/// OrganizationRequest) in presentation layer code (Commands/, Tui/).
/// Presentation code should use Application Services instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoSdkInPresentationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.NoSdkInPresentation,
        title: "No direct SDK types in presentation layer",
        messageFormat: "Use Application Services instead of direct SDK type '{0}' in presentation layer",
        category: DiagnosticCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Presentation layer code (Commands/, Tui/) must not use Dataverse SDK types directly. " +
                     "Use Application Services to abstract SDK interactions.");

    private static readonly string[] FlaggedTypeNames =
    {
        "Microsoft.PowerPlatform.Dataverse.Client.ServiceClient",
        "Microsoft.Xrm.Sdk.IOrganizationService",
        "Microsoft.Xrm.Sdk.OrganizationRequest",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSimpleMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!IsPresentationPath(filePath))
            return;

        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
        var type = typeInfo.Type;
        if (type is null)
            return;

        var fullName = type.ToDisplayString();
        if (Array.IndexOf(FlaggedTypeNames, fullName) < 0)
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            creation.GetLocation(),
            type.Name);

        context.ReportDiagnostic(diagnostic);
    }

    private static void AnalyzeSimpleMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!IsPresentationPath(filePath))
            return;

        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken);
        var symbol = symbolInfo.Symbol;
        if (symbol is null)
            return;

        // Check the containing type of the accessed member
        var containingType = symbol.ContainingType;
        if (containingType is null)
            return;

        var fullTypeName = containingType.ToDisplayString();
        if (Array.IndexOf(FlaggedTypeNames, fullTypeName) < 0)
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            memberAccess.GetLocation(),
            containingType.Name);

        context.ReportDiagnostic(diagnostic);
    }

    private static bool IsPresentationPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // Commands/Serve/ is excluded (MCP server exception)
        bool isServe = filePath.Contains("Commands/Serve/") || filePath.Contains("Commands\\Serve\\");
        if (isServe)
            return false;

        bool isTui = filePath.Contains("Tui/") || filePath.Contains("Tui\\");
        bool isCommands = filePath.Contains("Commands/") || filePath.Contains("Commands\\");

        return isTui || isCommands;
    }
}

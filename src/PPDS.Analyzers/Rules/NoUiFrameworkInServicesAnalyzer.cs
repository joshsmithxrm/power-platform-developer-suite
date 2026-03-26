using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS003: Detects usage of Terminal.Gui or Spectre.Console types in Application Services.
/// UI framework types must stay in the presentation layer.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoUiFrameworkInServicesAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.NoUiFrameworkInServices,
        title: "No UI framework types in Application Services",
        messageFormat: "UI framework type '{0}' used in Application Service; keep UI concerns in presentation layer",
        category: DiagnosticCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Application Services must not reference UI framework types (Terminal.Gui, Spectre.Console). " +
                     "Keep UI concerns in the presentation layer (Tui/, Commands/).");

    private static readonly string[] FlaggedNamespacePrefixes =
    {
        "Terminal.Gui",
        "Spectre.Console",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!IsServicesPath(filePath))
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var symbol = symbolInfo.Symbol;
        if (symbol is null)
            return;

        var containingType = symbol.ContainingType;
        if (containingType is null)
            return;

        if (IsUiFrameworkType(containingType))
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                invocation.GetLocation(),
                containingType.ToDisplayString());

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!IsServicesPath(filePath))
            return;

        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
        var type = typeInfo.Type;
        if (type is null)
            return;

        if (IsUiFrameworkType(type))
        {
            var diagnostic = Diagnostic.Create(
                Rule,
                creation.GetLocation(),
                type.ToDisplayString());

            context.ReportDiagnostic(diagnostic);
        }
    }

    private static bool IsUiFrameworkType(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null)
            return false;

        foreach (var prefix in FlaggedNamespacePrefixes)
        {
            if (ns == prefix || ns.StartsWith(prefix + "."))
                return true;
        }

        return false;
    }

    private static bool IsServicesPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        return filePath.Contains("PPDS.Cli/Services/") || filePath.Contains("PPDS.Cli\\Services\\");
    }
}

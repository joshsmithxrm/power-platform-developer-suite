using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS001: Detects direct System.IO.File/Directory static method calls or StreamReader/StreamWriter/FileStream
/// constructor calls in presentation layer code (Tui/ or Commands/).
/// File I/O should be delegated to Application Services.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoDirectFileIoInUiAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.NoDirectFileIoInUi,
        title: "No direct file I/O in presentation layer",
        messageFormat: "Direct file I/O '{0}' in presentation layer; move to an Application Service",
        category: DiagnosticCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Presentation layer code (Tui/, Commands/) must not perform direct file I/O. " +
                     "Move file operations to an Application Service.");

    private static readonly string[] FlaggedStaticTypes =
    {
        "System.IO.File",
        "System.IO.Directory",
    };

    private static readonly string[] FlaggedStreamTypes =
    {
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.FileStream",
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
        if (!IsPresentationPath(filePath))
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var symbol = symbolInfo.Symbol;
        if (symbol is null)
            return;

        var containingType = symbol.ContainingType;
        if (containingType is null)
            return;

        var fullTypeName = containingType.ToDisplayString();
        if (Array.IndexOf(FlaggedStaticTypes, fullTypeName) < 0)
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            $"{containingType.Name}.{symbol.Name}");

        context.ReportDiagnostic(diagnostic);
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
        if (Array.IndexOf(FlaggedStreamTypes, fullName) < 0)
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            creation.GetLocation(),
            type.Name);

        context.ReportDiagnostic(diagnostic);
    }

    private static bool IsPresentationPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // Commands/Serve/ is excluded (MCP server exception) — consistent with PPDS005
        bool isServe = filePath.Contains("PPDS.Cli/Commands/Serve/") || filePath.Contains("PPDS.Cli\\Commands\\Serve\\");
        if (isServe)
            return false;

        // Services/ is excluded — services are allowed to do file I/O
        bool isServices = filePath.Contains("PPDS.Cli/Services/") || filePath.Contains("PPDS.Cli\\Services\\");
        if (isServices)
            return false;

        // Must be in PPDS.Cli/Tui/ or PPDS.Cli/Commands/
        bool isTui = filePath.Contains("PPDS.Cli/Tui/") || filePath.Contains("PPDS.Cli\\Tui\\");
        bool isCommands = filePath.Contains("PPDS.Cli/Commands/") || filePath.Contains("PPDS.Cli\\Commands\\");

        return isTui || isCommands;
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS004: Detects raw exception throws in Application Services.
/// Services should throw PpdsException with ErrorCode for programmatic handling.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseStructuredExceptionsAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.UseStructuredExceptions,
        title: "Use PpdsException in Application Services",
        messageFormat: "Use PpdsException with ErrorCode instead of '{0}' in Application Services",
        category: DiagnosticCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Application Services must throw PpdsException with an ErrorCode " +
                     "instead of raw exceptions. Raw exceptions prevent programmatic error handling. " +
                     "See Constitution D4.");

    private static readonly string[] FlaggedTypeNames =
    {
        "System.Exception",
        "System.InvalidOperationException",
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.NotImplementedException",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeThrowStatement, SyntaxKind.ThrowStatement);
        context.RegisterSyntaxNodeAction(AnalyzeThrowExpression, SyntaxKind.ThrowExpression);
    }

    private static void AnalyzeThrowStatement(SyntaxNodeAnalysisContext context)
    {
        var throwStatement = (ThrowStatementSyntax)context.Node;

        // Skip bare re-throws: throw;
        if (throwStatement.Expression is null)
            return;

        AnalyzeThrowExpression(context, throwStatement.Expression, throwStatement.GetLocation());
    }

    private static void AnalyzeThrowExpression(SyntaxNodeAnalysisContext context)
    {
        var throwExpression = (ThrowExpressionSyntax)context.Node;
        AnalyzeThrowExpression(context, throwExpression.Expression, throwExpression.GetLocation());
    }

    private static void AnalyzeThrowExpression(
        SyntaxNodeAnalysisContext context,
        ExpressionSyntax expression,
        Location location)
    {
        // Only flag in Application Services (files with Services/ in path)
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!IsServicesPath(filePath))
            return;

        // Only flag object creation expressions (throw new X(...))
        if (expression is not ObjectCreationExpressionSyntax creation)
            return;

        var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
        var type = typeInfo.Type;
        if (type is null)
            return;

        // Skip PpdsException and its subclasses
        if (DerivesFromPpdsException(type))
            return;

        // Check if the type is one of the flagged exception types
        var fullName = type.ToDisplayString();
        if (Array.IndexOf(FlaggedTypeNames, fullName) < 0)
            return;

        var diagnostic = Diagnostic.Create(
            Rule,
            creation.GetLocation(),
            type.Name);

        context.ReportDiagnostic(diagnostic);
    }

    private static bool IsServicesPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        return filePath.Contains("Services/") || filePath.Contains("Services\\");
    }

    private static bool DerivesFromPpdsException(ITypeSymbol? type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.Name == "PpdsException")
                return true;
            current = current.BaseType;
        }

        return false;
    }
}

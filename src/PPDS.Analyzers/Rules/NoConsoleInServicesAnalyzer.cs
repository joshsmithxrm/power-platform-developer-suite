using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS002: Detects Console.Write/WriteLine/Error.Write/Error.WriteLine in Application Services.
/// Services should use IProgressReporter for output.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoConsoleInServicesAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.NoConsoleInServices,
        title: "No Console output in Application Services",
        messageFormat: "Use IProgressReporter instead of '{0}' in Application Services",
        category: DiagnosticCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Application Services must use IProgressReporter instead of Console output methods. " +
                     "Direct Console usage couples services to CLI output.");

    private static readonly string[] FlaggedMethodNames =
    {
        "Write",
        "WriteLine",
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
        var filePath = context.Node.SyntaxTree.FilePath;
        if (!IsServicesPath(filePath))
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;

        // Try semantic resolution first
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        var symbol = symbolInfo.Symbol as IMethodSymbol;

        if (symbol is not null)
        {
            // Check if method name is Write or WriteLine
            if (Array.IndexOf(FlaggedMethodNames, symbol.Name) < 0)
                return;

            var containingType = symbol.ContainingType;
            if (containingType is null)
                return;

            var fullTypeName = containingType.ToDisplayString();

            // Direct Console.Write/WriteLine
            if (fullTypeName == "System.Console")
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule, invocation.GetLocation(), $"Console.{symbol.Name}"));
                return;
            }

            // Console.Error.Write/WriteLine or Console.Out.Write/WriteLine — returns TextWriter
            var consoleProperty = GetConsolePropertyAccess(context, invocation);
            if (consoleProperty is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule, invocation.GetLocation(), $"Console.{consoleProperty}.{symbol.Name}"));
                return;
            }
        }

        // Fallback: if symbol didn't resolve (e.g. missing TextWriter reference),
        // check syntactically for Console.Error|Out.Write/Console.Error|Out.WriteLine
        if (symbol is null && invocation.Expression is MemberAccessExpressionSyntax fallbackMember)
        {
            var methodName = fallbackMember.Name.Identifier.Text;
            if (Array.IndexOf(FlaggedMethodNames, methodName) < 0)
                return;

            var fallbackProperty = GetConsolePropertyAccessSyntactic(context, fallbackMember);
            if (fallbackProperty is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule, invocation.GetLocation(), $"Console.{fallbackProperty}.{methodName}"));
            }
        }
    }

    private static readonly string[] ConsoleStreamProperties = { "Error", "Out" };

    private static string? GetConsolePropertyAccess(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Expression is MemberAccessExpressionSyntax parentAccess)
        {
            var parentSymbol = context.SemanticModel.GetSymbolInfo(parentAccess, context.CancellationToken).Symbol;
            if (parentSymbol is IPropertySymbol propertySymbol &&
                propertySymbol.ContainingType?.ToDisplayString() == "System.Console" &&
                Array.IndexOf(ConsoleStreamProperties, propertySymbol.Name) >= 0)
            {
                return propertySymbol.Name;
            }
        }

        return null;
    }

    private static string? GetConsolePropertyAccessSyntactic(
        SyntaxNodeAnalysisContext context,
        MemberAccessExpressionSyntax memberAccess)
    {
        // Match pattern: Console.Error|Out.Write/WriteLine
        if (memberAccess.Expression is not MemberAccessExpressionSyntax parentAccess)
            return null;

        var propertyName = parentAccess.Name.Identifier.Text;
        if (Array.IndexOf(ConsoleStreamProperties, propertyName) < 0)
            return null;

        // Verify "Console" resolves to System.Console
        var consoleSymbol = context.SemanticModel.GetSymbolInfo(parentAccess.Expression, context.CancellationToken).Symbol;
        if (consoleSymbol is INamedTypeSymbol namedType &&
            namedType.ToDisplayString() == "System.Console")
        {
            return propertyName;
        }

        return null;
    }

    private static bool IsServicesPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        return filePath.Contains("PPDS.Cli/Services/") || filePath.Contains("PPDS.Cli\\Services\\");
    }
}

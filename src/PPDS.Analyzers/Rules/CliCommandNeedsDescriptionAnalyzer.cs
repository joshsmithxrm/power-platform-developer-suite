using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS015: Requires every <c>System.CommandLine.Command</c>, <c>Option&lt;T&gt;</c>, or
/// <c>Argument&lt;T&gt;</c> construction site to carry a non-empty description. For
/// <c>Command</c>, description may be supplied as the 2-arg constructor's second
/// parameter or via an object initializer <c>Description = "..."</c>. For
/// <c>Option&lt;T&gt;</c> and <c>Argument&lt;T&gt;</c>, description must be supplied via
/// an object initializer (System.CommandLine's constructors take only name/aliases).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CliCommandNeedsDescriptionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.CliCommandNeedsDescription,
        title: "System.CommandLine Command/Option/Argument requires a Description",
        messageFormat: "{0} construction at this site requires a non-empty Description — pass it via the 2-arg constructor (Command only) or set Description via object initializer",
        category: DiagnosticCategories.Documentation,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every System.CommandLine Command, Option<T>, and Argument<T> created in " +
                     "CLI factory methods must expose a non-empty Description so generated reference docs " +
                     "and --help output are meaningful. See specs/docs-generation.md.");

    // Fully-qualified metadata names of the types we enforce. Generic arity
    // markers are part of the metadata name; Option`1 / Argument`1 catch the
    // typed forms and their subclasses.
    private const string CommandTypeName = "System.CommandLine.Command";
    private const string OptionGenericTypeName = "System.CommandLine.Option`1";
    private const string ArgumentGenericTypeName = "System.CommandLine.Argument`1";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeCreation,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void AnalyzeCreation(SyntaxNodeAnalysisContext context)
    {
        var node = context.Node;
        var typeInfo = context.SemanticModel.GetTypeInfo(node, context.CancellationToken);
        if (typeInfo.Type is not INamedTypeSymbol namedType)
            return;

        var kind = ClassifyTargetType(namedType);
        if (kind is null)
            return;

        // Extract argument list + initializer depending on expression shape.
        ArgumentListSyntax? argumentList = node switch
        {
            ObjectCreationExpressionSyntax oce => oce.ArgumentList,
            ImplicitObjectCreationExpressionSyntax ioce => ioce.ArgumentList,
            _ => null,
        };

        InitializerExpressionSyntax? initializer = node switch
        {
            ObjectCreationExpressionSyntax oce => oce.Initializer,
            ImplicitObjectCreationExpressionSyntax ioce => ioce.Initializer,
            _ => null,
        };

        if (HasDescriptionInInitializer(initializer))
            return;

        if (kind == TargetKind.Command && HasCommandDescriptionArgument(argumentList, context.SemanticModel, context.CancellationToken))
            return;

        var diagnostic = Diagnostic.Create(Rule, node.GetLocation(), kind.ToString());
        context.ReportDiagnostic(diagnostic);
    }

    private static TargetKind? ClassifyTargetType(INamedTypeSymbol type)
    {
        // Walk the type and its base chain. First match wins.
        for (var current = type; current is not null; current = current.BaseType)
        {
            var metadataName = current.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

            // Strip the generic-argument form so we match the open definition.
            // ConstructedFrom on Option<string> returns Option<T> whose display is
            // "System.CommandLine.Option<T>" — normalize to "System.CommandLine.Option`1".
            var normalized = NormalizeToMetadataName(current);

            if (normalized == CommandTypeName)
                return TargetKind.Command;
            if (normalized == OptionGenericTypeName)
                return TargetKind.Option;
            if (normalized == ArgumentGenericTypeName)
                return TargetKind.Argument;
        }

        return null;
    }

    private static string NormalizeToMetadataName(INamedTypeSymbol type)
    {
        var def = type.OriginalDefinition;
        var ns = def.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)) ?? string.Empty;
        var name = def.MetadataName; // includes `N arity suffix
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool HasDescriptionInInitializer(InitializerExpressionSyntax? initializer)
    {
        if (initializer is null)
            return false;

        foreach (var expr in initializer.Expressions)
        {
            if (expr is not AssignmentExpressionSyntax { Left: IdentifierNameSyntax ident } assignment)
                continue;

            if (ident.Identifier.Text != "Description")
                continue;

            if (IsNonEmptyStringExpression(assignment.Right))
                return true;
        }

        return false;
    }

    private static bool HasCommandDescriptionArgument(
        ArgumentListSyntax? argumentList,
        SemanticModel model,
        CancellationToken ct)
    {
        if (argumentList is null)
            return false;

        // System.CommandLine.Command has a (string name, string? description = null)
        // constructor. We accept any call site that passes a non-empty string
        // expression in the description position — identified by matching the
        // parameter name "description" on the resolved symbol.
        var args = argumentList.Arguments;
        if (args.Count < 2)
            return false;

        var symbolInfo = model.GetSymbolInfo(argumentList.Parent!, ct);
        if (symbolInfo.Symbol is IMethodSymbol ctor)
        {
            for (var i = 0; i < args.Count && i < ctor.Parameters.Length; i++)
            {
                var paramName = args[i].NameColon?.Name.Identifier.Text ?? ctor.Parameters[i].Name;
                if (paramName == "description" && IsNonEmptyStringExpression(args[i].Expression))
                    return true;
            }
        }

        return false;
    }

    private static bool IsNonEmptyStringExpression(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return !string.IsNullOrWhiteSpace(literal.Token.ValueText);
            case InterpolatedStringExpressionSyntax interpolated:
                return interpolated.Contents.Count > 0;
            case LiteralExpressionSyntax n when n.IsKind(SyntaxKind.NullLiteralExpression):
                return false;
            default:
                // Identifier references, method calls, etc. — we can't prove emptiness
                // statically without data-flow. Treat as "non-empty" to avoid false
                // positives; the runtime caller sees the value.
                return true;
        }
    }

    private enum TargetKind
    {
        Command,
        Option,
        Argument,
    }
}

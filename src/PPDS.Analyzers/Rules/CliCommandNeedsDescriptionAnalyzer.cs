using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS015: Requires every concrete Spectre.Console.Cli command class to expose a
/// non-empty <c>Description</c> — supplied via <c>[System.ComponentModel.Description]</c>
/// attribute, a <c>Description</c> property/field initializer, or a constructor assignment.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CliCommandNeedsDescriptionAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.CliCommandNeedsDescription,
        title: "Spectre CLI command requires a Description",
        messageFormat: "CLI command '{0}' requires a Description — set via [Description(...)] attribute, Description property initializer, or constructor assignment",
        category: DiagnosticCategories.Documentation,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Concrete subclasses of Spectre.Console.Cli.Command, Command<TSettings>, AsyncCommand, " +
                     "or AsyncCommand<TSettings> must provide a non-empty Description so generated reference docs " +
                     "and CLI help text are meaningful. See specs/docs-generation.md.");

    private const string DescriptionAttributeFullName = "System.ComponentModel.DescriptionAttribute";

    private static readonly ImmutableHashSet<string> SpectreCommandBaseTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Spectre.Console.Cli.Command",
        "Spectre.Console.Cli.AsyncCommand");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        if (context.Symbol is not INamedTypeSymbol type)
            return;

        // Only concrete classes are considered.
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        if (type.IsImplicitlyDeclared)
            return;

        if (!InheritsFromSpectreCommand(type))
            return;

        if (HasDescriptionAttribute(type))
            return;

        if (HasDescriptionMemberInitializer(type, context.CancellationToken))
            return;

        if (HasConstructorDescriptionAssignment(type, context.CancellationToken))
            return;

        var location = type.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
        var diagnostic = Diagnostic.Create(Rule, location, type.Name);
        context.ReportDiagnostic(diagnostic);
    }

    private static bool InheritsFromSpectreCommand(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            // Use the original (unconstructed) definition so Command<TSettings> matches Command<T>.
            var originalName = current.OriginalDefinition.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                    SymbolDisplayGlobalNamespaceStyle.Omitted));

            // Strip generic arity (e.g. "Spectre.Console.Cli.Command<TSettings>" -> "Spectre.Console.Cli.Command")
            var backtick = originalName.IndexOf('<');
            var baseName = backtick >= 0 ? originalName.Substring(0, backtick) : originalName;

            if (SpectreCommandBaseTypes.Contains(baseName))
                return true;
        }

        return false;
    }

    private static bool HasDescriptionAttribute(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            var attrClass = attribute.AttributeClass;
            if (attrClass is null)
                continue;

            if (attrClass.ToDisplayString() != DescriptionAttributeFullName)
                continue;

            if (attribute.ConstructorArguments.Length == 0)
                continue;

            var arg = attribute.ConstructorArguments[0];
            if (arg.Value is string text && !string.IsNullOrWhiteSpace(text))
                return true;
        }

        return false;
    }

    private static bool HasDescriptionMemberInitializer(INamedTypeSymbol type, CancellationToken ct)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax(ct);
            if (node is not TypeDeclarationSyntax typeDecl)
                continue;

            foreach (var member in typeDecl.Members)
            {
                if (member is PropertyDeclarationSyntax property
                    && property.Identifier.Text == "Description"
                    && property.Initializer is { Value: var propValue }
                    && IsNonEmptyStringLiteral(propValue))
                {
                    return true;
                }

                if (member is FieldDeclarationSyntax field)
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        if (variable.Identifier.Text == "Description"
                            && variable.Initializer is { Value: var fieldValue }
                            && IsNonEmptyStringLiteral(fieldValue))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool HasConstructorDescriptionAssignment(INamedTypeSymbol type, CancellationToken ct)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var node = syntaxRef.GetSyntax(ct);
            if (node is not TypeDeclarationSyntax typeDecl)
                continue;

            foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
            {
                if (ctor.Body is null && ctor.ExpressionBody is null)
                    continue;

                var assignments = ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>();
                foreach (var assignment in assignments)
                {
                    if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
                        continue;

                    var leftName = GetAssignmentTargetName(assignment.Left);
                    if (leftName != "Description")
                        continue;

                    if (IsNonEmptyStringLiteral(assignment.Right))
                        return true;
                }
            }
        }

        return false;
    }

    private static string? GetAssignmentTargetName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax
            => memberAccess.Name.Identifier.Text,
        _ => null,
    };

    private static bool IsNonEmptyStringLiteral(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return !string.IsNullOrWhiteSpace(literal.Token.ValueText);
        }

        return false;
    }
}

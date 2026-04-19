using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS016: Detects methods with <c>[McpServerTool]</c> that are missing required
/// documentation metadata. The tool name must be set via the <c>Name</c> named
/// argument on <c>[McpServerTool]</c>. The description may be supplied either via
/// a <c>Description</c> named argument on <c>[McpServerTool]</c> OR via a sibling
/// <c>[System.ComponentModel.Description]</c> attribute on the same method — the
/// PPDS codebase convention is the sibling-attribute pattern.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class McpToolNeedsMetadataAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.McpToolNeedsMetadata,
        title: "MCP tool requires Name and Description metadata",
        messageFormat: "MCP tool '{0}' requires '{1}' on [McpServerTool] attribute",
        category: DiagnosticCategories.Documentation,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Methods marked with [McpServerTool] must set both the Name and Description " +
                     "named arguments so the tool can be discovered and documented by downstream generators.");

    private const string McpServerToolAttributeName = "McpServerToolAttribute";
    private const string DescriptionAttributeName = "DescriptionAttribute";
    private const string NameArgument = "Name";
    private const string DescriptionArgument = "Description";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;

        AttributeData? mcpAttribute = null;
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == McpServerToolAttributeName)
            {
                mcpAttribute = attribute;
                break;
            }
        }

        if (mcpAttribute is null)
            return;

        var hasName = HasNonEmptyNamedArgument(mcpAttribute, NameArgument);
        var hasDescription =
            HasNonEmptyNamedArgument(mcpAttribute, DescriptionArgument)
            || HasSiblingDescriptionAttribute(method);

        var location = GetAttributeLocation(mcpAttribute) ?? method.Locations.FirstOrDefault();
        if (location is null)
            return;

        if (!hasName)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, location, method.Name, NameArgument));
        }

        if (!hasDescription)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, location, method.Name, DescriptionArgument));
        }
    }

    private static bool HasNonEmptyNamedArgument(AttributeData attribute, string argumentName)
    {
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key != argumentName)
                continue;

            var typedConstant = named.Value;
            if (typedConstant.IsNull)
                return false;

            // Only string values count as metadata; reject null or empty strings.
            if (typedConstant.Value is string s)
                return !string.IsNullOrEmpty(s);

            // Non-string values are unexpected but treat presence as "set" to avoid false positives.
            return typedConstant.Value is not null;
        }

        return false;
    }

    private static Location? GetAttributeLocation(AttributeData attribute)
    {
        var syntaxRef = attribute.ApplicationSyntaxReference;
        return syntaxRef?.GetSyntax().GetLocation();
    }

    /// <summary>
    /// Returns true if the method has a sibling
    /// <c>[System.ComponentModel.Description("...")]</c> attribute with a non-empty
    /// string argument — the PPDS codebase convention for MCP tool descriptions.
    /// </summary>
    private static bool HasSiblingDescriptionAttribute(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            var attrClass = attribute.AttributeClass;
            if (attrClass is null)
                continue;

            // Match by simple name OR by short name `Description` (handles C#'s
            // convention of dropping the "Attribute" suffix at the use site).
            if (attrClass.Name != DescriptionAttributeName
                && attrClass.Name != "Description")
            {
                continue;
            }

            // Only accept the BCL's System.ComponentModel.DescriptionAttribute,
            // not the McpServerTool stub's `Description` property or a custom
            // attribute named Description in a different namespace.
            var ns = attrClass.ContainingNamespace;
            if (ns is null || ns.ToDisplayString() != "System.ComponentModel")
                continue;

            // DescriptionAttribute takes the description as its sole constructor argument.
            // Try semantic binding first (preferred).
            if (attribute.ConstructorArguments.Length > 0)
            {
                var first = attribute.ConstructorArguments[0];
                if (!first.IsNull && first.Value is string s && !string.IsNullOrEmpty(s))
                    return true;
            }

            // Fallback: read the argument from the syntax tree. This handles
            // partial compilations where the System.Attribute base class could
            // not be bound (rare but happens in test fixtures).
            if (HasNonEmptyStringArgInSyntax(attribute))
                return true;
        }

        return false;
    }

    private static bool HasNonEmptyStringArgInSyntax(AttributeData attribute)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
        if (syntax?.ArgumentList is null || syntax.ArgumentList.Arguments.Count == 0)
            return false;

        var firstArg = syntax.ArgumentList.Arguments[0];
        if (firstArg.Expression is LiteralExpressionSyntax literal
            && literal.Token.Value is string s
            && !string.IsNullOrEmpty(s))
        {
            return true;
        }

        return false;
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS006: Detects string literals in QueryExpression constructor.
/// Use early-bound EntityLogicalName constants for type safety.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseEarlyBoundEntitiesAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.UseEarlyBoundEntities,
        title: "Use early-bound entity constants",
        messageFormat: "Use '{0}.EntityLogicalName' instead of string literal \"{1}\"",
        category: DiagnosticCategories.Style,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "QueryExpression should use early-bound EntityLogicalName constants for compile-time safety. " +
                     "See CODE_SCANNING.md for details on available generated entities.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var objectCreation = (ObjectCreationExpressionSyntax)context.Node;

        // Check if it's a QueryExpression constructor using full metadata name for robustness
        var typeInfo = context.SemanticModel.GetTypeInfo(objectCreation, context.CancellationToken);
        var queryExpressionType = context.SemanticModel.Compilation.GetTypeByMetadataName("Microsoft.Xrm.Sdk.Query.QueryExpression");
        if (queryExpressionType is null || !SymbolEqualityComparer.Default.Equals(typeInfo.Type, queryExpressionType))
            return;

        // Check if the first argument is a string literal
        var arguments = objectCreation.ArgumentList?.Arguments;
        if (arguments is null || arguments.Value.Count == 0)
            return;

        var firstArg = arguments.Value[0].Expression;
        if (firstArg is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return;

        var entityName = literal.Token.ValueText;

        // Map known entity names to their early-bound class names
        var suggestedClass = GetEarlyBoundClassName(entityName);

        var diagnostic = Diagnostic.Create(
            Rule,
            literal.GetLocation(),
            suggestedClass,
            entityName);

        context.ReportDiagnostic(diagnostic);
    }

    private static string GetEarlyBoundClassName(string entityLogicalName)
    {
        // Map common entity names to their PascalCase class names
        return entityLogicalName switch
        {
            "pluginassembly" => "PluginAssembly",
            "pluginpackage" => "PluginPackage",
            "plugintype" => "PluginType",
            "sdkmessage" => "SdkMessage",
            "sdkmessagefilter" => "SdkMessageFilter",
            "sdkmessageprocessingstep" => "SdkMessageProcessingStep",
            "sdkmessageprocessingstepimage" => "SdkMessageProcessingStepImage",
            "solution" => "Solution",
            "solutioncomponent" => "SolutionComponent",
            "asyncoperation" => "AsyncOperation",
            "importjob" => "ImportJob",
            "systemuser" => "SystemUser",
            "role" => "Role",
            "publisher" => "Publisher",
            "environmentvariabledefinition" => "EnvironmentVariableDefinition",
            "environmentvariablevalue" => "EnvironmentVariableValue",
            "workflow" => "Workflow",
            "connectionreference" => "ConnectionReference",
            "plugintracelog" => "PluginTraceLog",
            _ => ToPascalCase(entityLogicalName)
        };
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Simple conversion: capitalize first letter
        return char.ToUpperInvariant(input[0]) + input.Substring(1);
    }
}

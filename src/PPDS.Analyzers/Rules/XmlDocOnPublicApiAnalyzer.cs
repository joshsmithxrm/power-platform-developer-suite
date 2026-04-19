using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Rules;

/// <summary>
/// PPDS014: Requires every public type or public member in the four PPDS library projects
/// (PPDS.Dataverse, PPDS.Migration, PPDS.Auth, PPDS.Plugins) to have either a
/// <c>/// &lt;summary&gt;</c> XML doc comment or be annotated with
/// <c>[EditorBrowsable(EditorBrowsableState.Never)]</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XmlDocOnPublicApiAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.XmlDocOnPublicApi,
        title: "Public API requires XML documentation",
        messageFormat: "Public {0} '{1}' requires '/// <summary>' or [EditorBrowsable(Never)]",
        category: DiagnosticCategories.Documentation,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Public types and members in PPDS library projects must carry a '/// <summary>' XML doc comment " +
                     "or be marked '[EditorBrowsable(EditorBrowsableState.Never)]'. See specs/docs-generation.md.");

    private static readonly ImmutableHashSet<string> LibraryAssemblyNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "PPDS.Dataverse",
        "PPDS.Migration",
        "PPDS.Auth",
        "PPDS.Plugins");

    private const string EditorBrowsableAttributeFullName = "System.ComponentModel.EditorBrowsableAttribute";
    private const string GeneratedCodeAttributeFullName = "System.CodeDom.Compiler.GeneratedCodeAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Scope: only run when the compilation's assembly name matches one of the four PPDS libraries.
        var assemblyName = context.Compilation.AssemblyName;
        if (assemblyName is null || !LibraryAssemblyNames.Contains(assemblyName))
            return;

        context.RegisterSymbolAction(
            AnalyzeSymbol,
            SymbolKind.NamedType,
            SymbolKind.Method,
            SymbolKind.Property,
            SymbolKind.Field,
            SymbolKind.Event);
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        var symbol = context.Symbol;

        // Only public symbols.
        if (symbol.DeclaredAccessibility != Accessibility.Public)
            return;

        // Only publicly reachable: every containing type must also be public.
        for (var containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            if (containing.DeclaredAccessibility != Accessibility.Public)
                return;
        }

        // Methods that are compiler-generated for properties/events should not flag on their own.
        if (symbol is IMethodSymbol method && method.AssociatedSymbol is not null)
            return;

        // Implicitly-declared symbols (default ctor, backing fields, etc.) are never author-written.
        if (symbol.IsImplicitlyDeclared)
            return;

        // Skip if the symbol or any containing type carries [EditorBrowsable(Never)] or [GeneratedCode].
        if (HasSuppressingAttribute(symbol))
            return;

        // Skip generated code by file path.
        if (IsGeneratedPath(symbol))
            return;

        var xml = symbol.GetDocumentationCommentXml(cancellationToken: context.CancellationToken);
        if (!string.IsNullOrWhiteSpace(xml))
            return;

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
        var kind = GetSymbolKindDisplay(symbol);
        var diagnostic = Diagnostic.Create(Rule, location, kind, symbol.Name);
        context.ReportDiagnostic(diagnostic);
    }

    private static bool HasSuppressingAttribute(ISymbol symbol)
    {
        if (HasAttributeOnSymbol(symbol))
            return true;

        for (var containing = symbol.ContainingType; containing is not null; containing = containing.ContainingType)
        {
            if (HasAttributeOnSymbol(containing))
                return true;
        }

        return false;
    }

    private static bool HasAttributeOnSymbol(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            var attrClass = attribute.AttributeClass;
            if (attrClass is null)
                continue;

            var fullName = attrClass.ToDisplayString();

            if (fullName == GeneratedCodeAttributeFullName)
                return true;

            if (fullName == EditorBrowsableAttributeFullName && IsEditorBrowsableNever(attribute))
                return true;
        }

        return false;
    }

    private static bool IsEditorBrowsableNever(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
            return false;

        var arg = attribute.ConstructorArguments[0];

        // EditorBrowsableState.Never has the value 1.
        if (arg.Value is int intValue)
            return intValue == 1;

        return false;
    }

    private static bool IsGeneratedPath(ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource)
                continue;

            var path = location.SourceTree?.FilePath;
            if (string.IsNullOrEmpty(path))
                continue;

            if (path!.Contains("/Generated/") ||
                path.Contains("\\Generated\\") ||
                path.Contains("/obj/") ||
                path.Contains("\\obj\\"))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetSymbolKindDisplay(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol namedType => namedType.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "type",
        },
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "member",
    };
}

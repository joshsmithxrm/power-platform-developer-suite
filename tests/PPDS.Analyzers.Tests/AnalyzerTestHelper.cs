using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PPDS.Analyzers.Tests;

/// <summary>
/// Helper to compile source code and run a Roslyn analyzer against it.
/// </summary>
internal static class AnalyzerTestHelper
{
    private static readonly MetadataReference[] SharedReferences = BuildSharedReferences();

    /// <summary>
    /// Compiles <paramref name="source"/> and returns diagnostics from <typeparamref name="TAnalyzer"/>.
    /// </summary>
    /// <param name="source">C# source code to analyze.</param>
    /// <param name="filePath">Optional file path for the syntax tree (used by path-scoped analyzers).</param>
    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync<TAnalyzer>(
        string source,
        string? filePath = null)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath ?? "Test.cs");

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees: new[] { tree },
            references: SharedReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = new TAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        // Return only diagnostics from our analyzer (filter by supported IDs)
        var supportedIds = analyzer.SupportedDiagnostics.Select(d => d.Id).ToHashSet();
        return diagnostics.Where(d => supportedIds.Contains(d.Id)).ToImmutableArray();
    }

    private static MetadataReference[] BuildSharedReferences()
    {
        // Reference the runtime assemblies needed for compilation
        var assemblies = new[]
        {
            typeof(object).Assembly,                                    // mscorlib / System.Runtime
            typeof(System.Threading.Tasks.Task).Assembly,               // System.Threading.Tasks
            typeof(System.Threading.CancellationToken).Assembly,        // System.Threading
            typeof(System.Linq.Enumerable).Assembly,                    // System.Linq
            typeof(System.Collections.Generic.List<>).Assembly,         // System.Collections
            typeof(Console).Assembly,                                   // System.Console
            typeof(IAsyncDisposable).Assembly,                          // System.Runtime (IAsyncDisposable)
        };

        // Deduplicate assemblies that might resolve to the same location
        return assemblies
            .Select(a => a.Location)
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct()
            .Select(l => (MetadataReference)MetadataReference.CreateFromFile(l))
            .ToArray();
    }
}

using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class XmlDocOnPublicApiAnalyzerTests
{
    private const string LibraryAssembly = "PPDS.Dataverse";

    /// <summary>AC-01: Bare public type in a library project with no summary should fire PPDS014.</summary>
    [Fact]
    public async Task FlagsUndocumentedPublicType()
    {
        const string code = """
            namespace PPDS.Dataverse
            {
                public class Foo
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<XmlDocOnPublicApiAnalyzer>(
            code,
            filePath: "src/PPDS.Dataverse/Foo.cs",
            assemblyName: LibraryAssembly);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS014");
        diagnostics[0].GetMessage().Should().Contain("Foo");
    }

    /// <summary>AC-02: Documented type with undocumented public member — member should fire PPDS014.</summary>
    [Fact]
    public async Task FlagsUndocumentedPublicMember()
    {
        const string code = """
            namespace PPDS.Dataverse
            {
                /// <summary>Documented type.</summary>
                public class Foo
                {
                    public void Bar() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<XmlDocOnPublicApiAnalyzer>(
            code,
            filePath: "src/PPDS.Dataverse/Foo.cs",
            assemblyName: LibraryAssembly);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS014");
        diagnostics[0].GetMessage().Should().Contain("Bar");
    }

    /// <summary>AC-03: [EditorBrowsable(Never)] on a public type should suppress PPDS014.</summary>
    [Fact]
    public async Task AllowsEditorBrowsableNever()
    {
        const string code = """
            using System.ComponentModel;

            namespace PPDS.Dataverse
            {
                [EditorBrowsable(EditorBrowsableState.Never)]
                public class Foo
                {
                    public void Bar() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<XmlDocOnPublicApiAnalyzer>(
            code,
            filePath: "src/PPDS.Dataverse/Foo.cs",
            assemblyName: LibraryAssembly);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-04: Fully documented type and members — no diagnostics.</summary>
    [Fact]
    public async Task AllowsFullyDocumentedType()
    {
        const string code = """
            namespace PPDS.Dataverse
            {
                /// <summary>Documented type.</summary>
                public class Foo
                {
                    /// <summary>Documented method.</summary>
                    public void Bar() { }

                    /// <summary>Documented property.</summary>
                    public int Count { get; set; }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<XmlDocOnPublicApiAnalyzer>(
            code,
            filePath: "src/PPDS.Dataverse/Foo.cs",
            assemblyName: LibraryAssembly);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-05: [GeneratedCode] attribute should suppress PPDS014.</summary>
    [Fact]
    public async Task SkipsGeneratedCodeAttribute()
    {
        const string code = """
            using System.CodeDom.Compiler;

            namespace PPDS.Dataverse
            {
                [GeneratedCode("generator", "1.0")]
                public class Foo
                {
                    public void Bar() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<XmlDocOnPublicApiAnalyzer>(
            code,
            filePath: "src/PPDS.Dataverse/Foo.cs",
            assemblyName: LibraryAssembly);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-06: Files under a /Generated/ directory should be skipped.</summary>
    [Fact]
    public async Task SkipsGeneratedDirectory()
    {
        const string code = """
            namespace PPDS.Dataverse
            {
                public class Foo
                {
                    public void Bar() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<XmlDocOnPublicApiAnalyzer>(
            code,
            filePath: "src/PPDS.Dataverse/Generated/Foo.cs",
            assemblyName: LibraryAssembly);

        diagnostics.Should().BeEmpty();
    }
}

using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class McpToolNeedsMetadataAnalyzerTests
{
    /// <summary>
    /// Minimal stub for the <c>[McpServerTool]</c> attribute so the test
    /// compilation doesn't need to reference the ModelContextProtocol package.
    /// </summary>
    private const string McpServerToolAttributeStub = """
        [System.AttributeUsage(System.AttributeTargets.Method)]
        public sealed class McpServerToolAttribute : System.Attribute
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
        }
        """;

    /// <summary>AC-11: method with [McpServerTool(Description = "x")] but no Name should flag.</summary>
    [Fact]
    public async Task PPDS016_FlagsMissingName()
    {
        var code = $$"""
            {{McpServerToolAttributeStub}}

            public class MyTool
            {
                [McpServerTool(Description = "does a thing")]
                public void Foo() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<McpToolNeedsMetadataAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Should().Match<Microsoft.CodeAnalysis.Diagnostic>(d =>
                d.Id == "PPDS016" &&
                d.GetMessage().Contains("Name") &&
                d.GetMessage().Contains("Foo"));
    }

    /// <summary>AC-12: method with [McpServerTool(Name = "x")] but no Description (neither named arg nor sibling attribute) should flag.</summary>
    [Fact]
    public async Task PPDS016_FlagsMissingDescription()
    {
        var code = $$"""
            {{McpServerToolAttributeStub}}

            public class MyTool
            {
                [McpServerTool(Name = "ppds_foo")]
                public void Foo() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<McpToolNeedsMetadataAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Should().Match<Microsoft.CodeAnalysis.Diagnostic>(d =>
                d.Id == "PPDS016" &&
                d.GetMessage().Contains("Description") &&
                d.GetMessage().Contains("Foo"));
    }

    /// <summary>
    /// Description may be supplied via a sibling [System.ComponentModel.Description]
    /// attribute (the PPDS codebase convention) instead of the Description named
    /// argument on [McpServerTool]. Either form satisfies the requirement.
    /// </summary>
    [Fact]
    public async Task PPDS016_AllowsSiblingDescriptionAttribute()
    {
        var code = $$"""
            {{McpServerToolAttributeStub}}

            public class MyTool
            {
                [McpServerTool(Name = "ppds_foo")]
                [System.ComponentModel.Description("does a thing")]
                public void Foo() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<McpToolNeedsMetadataAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }
}

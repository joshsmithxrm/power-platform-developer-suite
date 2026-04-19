using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class CliCommandNeedsDescriptionAnalyzerTests
{
    /// <summary>
    /// Minimal System.CommandLine stubs. We only need the types the analyzer
    /// recognizes by fully-qualified name — the real package is not referenced
    /// because the test harness compiles fixtures in isolation.
    /// </summary>
    private const string CommandLineStubs = """
        namespace System.CommandLine
        {
            public class Command
            {
                public Command(string name) { Name = name; }
                public Command(string name, string? description = null) { Name = name; Description = description; }
                public string Name { get; }
                public string? Description { get; set; }
            }

            public class Option<T>
            {
                public Option(string name, params string[] aliases) { Name = name; }
                public string Name { get; }
                public string? Description { get; set; }
            }

            public class Argument<T>
            {
                public Argument(string name) { Name = name; }
                public string Name { get; }
                public string? Description { get; set; }
            }
        }
        """;

    private const string TestHarness = """
        namespace TestApp
        {
            public static class Factory
            {
                public static System.CommandLine.Command Make() => BODY;
            }
        }
        """;

    /// <summary>AC-08: a Command constructed with only a name (no description) flags PPDS015.</summary>
    [Fact]
    public async Task PPDS015_FlagsCommandWithoutDescription()
    {
        var source = CommandLineStubs + "\n" + TestHarness
            .Replace("BODY", """new System.CommandLine.Command("auth")""");

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(source);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS015");
    }

    /// <summary>AC-09: Command with 2-arg ctor (name, description) satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsCommandWithCtorDescription()
    {
        var source = CommandLineStubs + "\n" + TestHarness
            .Replace("BODY", """new System.CommandLine.Command("auth", "Manage authentication profiles")""");

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(source);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-10: Command with single-arg ctor plus object initializer Description satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsCommandWithInitializerDescription()
    {
        var source = CommandLineStubs + "\n" + TestHarness
            .Replace("BODY", """new System.CommandLine.Command("auth") { Description = "Manage authentication profiles" }""");

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(source);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-08 (Option): Option&lt;T&gt; without a Description in its initializer flags PPDS015.</summary>
    [Fact]
    public async Task PPDS015_FlagsOptionWithoutDescription()
    {
        const string body = """
            namespace TestApp
            {
                public static class Factory
                {
                    public static void Make()
                    {
                        var opt = new System.CommandLine.Option<string>("--name", "-n");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(CommandLineStubs + "\n" + body);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS015");
    }

    /// <summary>AC-09 (Option): Option&lt;T&gt; with Description in initializer satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsOptionWithInitializerDescription()
    {
        const string body = """
            namespace TestApp
            {
                public static class Factory
                {
                    public static void Make()
                    {
                        var opt = new System.CommandLine.Option<string>("--name", "-n")
                        {
                            Description = "The thing"
                        };
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(CommandLineStubs + "\n" + body);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Argument&lt;T&gt; without a Description flags PPDS015.</summary>
    [Fact]
    public async Task PPDS015_FlagsArgumentWithoutDescription()
    {
        const string body = """
            namespace TestApp
            {
                public static class Factory
                {
                    public static void Make()
                    {
                        var arg = new System.CommandLine.Argument<string>("input");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(CommandLineStubs + "\n" + body);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS015");
    }

    /// <summary>Argument&lt;T&gt; with initializer Description satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsArgumentWithInitializerDescription()
    {
        const string body = """
            namespace TestApp
            {
                public static class Factory
                {
                    public static void Make()
                    {
                        var arg = new System.CommandLine.Argument<string>("input")
                        {
                            Description = "Input path"
                        };
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(CommandLineStubs + "\n" + body);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// Command with empty-string description in its ctor still flags — whitespace-only
    /// doesn't satisfy the "non-empty" requirement.
    /// </summary>
    [Fact]
    public async Task PPDS015_FlagsCommandWithEmptyCtorDescription()
    {
        var source = CommandLineStubs + "\n" + TestHarness
            .Replace("BODY", """new System.CommandLine.Command("auth", "")""");

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(source);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS015");
    }

    /// <summary>
    /// Subclasses of System.CommandLine.Command (e.g. RootCommand) fall under the
    /// same rule — object initializer description satisfies it.
    /// </summary>
    [Fact]
    public async Task PPDS015_AppliesToCommandSubclasses()
    {
        const string body = """
            namespace System.CommandLine
            {
                public class RootCommand : Command
                {
                    public RootCommand() : base("root") { }
                }
            }

            namespace TestApp
            {
                public static class Factory
                {
                    public static void Make()
                    {
                        // No description in initializer — must flag.
                        var root = new System.CommandLine.RootCommand();
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(CommandLineStubs + "\n" + body);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS015");
    }
}

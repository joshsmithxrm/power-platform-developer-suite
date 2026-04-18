using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class CliCommandNeedsDescriptionAnalyzerTests
{
    /// <summary>
    /// Shared Spectre stubs and a Settings stub. Kept minimal — we only need types the analyzer
    /// can match by metadata name. The real Spectre assembly is not referenced because the test
    /// harness compiles fixtures in isolation.
    /// </summary>
    private const string SpectreStubs = """
        namespace Spectre.Console.Cli
        {
            public abstract class CommandSettings { }
            public abstract class Command
            {
                public virtual int Execute() { return 0; }
            }
            public abstract class Command<TSettings> where TSettings : CommandSettings
            {
                public virtual int Execute(TSettings settings) { return 0; }
            }
            public abstract class AsyncCommand
            {
                public virtual System.Threading.Tasks.Task<int> ExecuteAsync() { return System.Threading.Tasks.Task.FromResult(0); }
            }
            public abstract class AsyncCommand<TSettings> where TSettings : CommandSettings
            {
                public virtual System.Threading.Tasks.Task<int> ExecuteAsync(TSettings settings) { return System.Threading.Tasks.Task.FromResult(0); }
            }
        }

        namespace System.ComponentModel
        {
            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class DescriptionAttribute : System.Attribute
            {
                public DescriptionAttribute(string description) { Description = description; }
                public string Description { get; }
            }
        }

        namespace TestApp
        {
            public sealed class FooSettings : Spectre.Console.Cli.CommandSettings { }
        }
        """;

    /// <summary>AC-08: a Spectre command subclass with no Description flags PPDS015.</summary>
    [Fact]
    public async Task PPDS015_FlagsMissingDescription()
    {
        const string body = """
            namespace TestApp
            {
                public class FooCommand : Spectre.Console.Cli.Command<FooSettings>
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(SpectreStubs + "\n" + body);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS015");
    }

    /// <summary>AC-09: `[Description("...")]` on the type satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsAttributeDescription()
    {
        const string body = """
            namespace TestApp
            {
                [System.ComponentModel.Description("bar")]
                public class FooCommand : Spectre.Console.Cli.Command<FooSettings>
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(SpectreStubs + "\n" + body);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-10 (a): a Description property initializer satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsPropertyInitializerDescription()
    {
        const string body = """
            namespace TestApp
            {
                public class FooCommand : Spectre.Console.Cli.Command<FooSettings>
                {
                    public string Description { get; } = "x";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(SpectreStubs + "\n" + body);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-10 (b): a constructor assignment of Description satisfies the rule.</summary>
    [Fact]
    public async Task PPDS015_AllowsConstructorAssignmentDescription()
    {
        const string body = """
            namespace TestApp
            {
                public class FooCommand : Spectre.Console.Cli.Command<FooSettings>
                {
                    public string Description { get; set; }

                    public FooCommand()
                    {
                        Description = "x";
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<CliCommandNeedsDescriptionAnalyzer>(SpectreStubs + "\n" + body);

        diagnostics.Should().BeEmpty();
    }
}

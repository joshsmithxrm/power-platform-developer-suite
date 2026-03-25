using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

public class NoConsoleInServicesAnalyzerTests
{
    /// <summary>Console.WriteLine() in Services/ should flag.</summary>
    [Fact]
    public async Task PPDS002_ConsoleWriteLineInService_ReportsWarning()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork()
                {
                    Console.WriteLine("progress...");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoConsoleInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS002");
    }

    /// <summary>Console.Error.WriteLine() in Services/ should flag.</summary>
    [Fact]
    public async Task PPDS002_ConsoleErrorWriteLineInService_ReportsWarning()
    {
        const string code = """
            using System;
            using System.IO;
            class MyService
            {
                void DoWork()
                {
                    Console.Error.WriteLine("error occurred");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoConsoleInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS002");
    }

    /// <summary>Console.WriteLine() in Commands/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS002_ConsoleWriteLineInCommand_NoDiagnostic()
    {
        const string code = """
            using System;
            class MyCommand
            {
                void Execute()
                {
                    Console.WriteLine("output");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoConsoleInServicesAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Console.ReadLine() in Services/ should NOT flag (input, not output).</summary>
    [Fact]
    public async Task PPDS002_ConsoleReadLineInService_NoDiagnostic()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork()
                {
                    var input = Console.ReadLine();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoConsoleInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Non-Console code in Services/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS002_NonConsoleCallInService_NoDiagnostic()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork()
                {
                    var x = Math.Max(1, 2);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoConsoleInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }
}

using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class NoUiFrameworkInServicesAnalyzerTests
{
    /// <summary>Terminal.Gui.MessageBox.Query() in Services/ should flag.</summary>
    [Fact]
    public async Task PPDS003_TerminalGuiTypeInService_ReportsWarning()
    {
        const string code = """
            namespace Terminal.Gui
            {
                public class MessageBox
                {
                    public static int Query(string title, string msg) => 0;
                }
            }

            namespace MyApp
            {
                class MyService
                {
                    void DoWork()
                    {
                        Terminal.Gui.MessageBox.Query("Title", "Message");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoUiFrameworkInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS003");
    }

    /// <summary>Spectre.Console.AnsiConsole.Write() in Services/ should flag.</summary>
    [Fact]
    public async Task PPDS003_SpectreConsoleTypeInService_ReportsWarning()
    {
        const string code = """
            namespace Spectre.Console
            {
                public class AnsiConsole
                {
                    public static void Write(string text) { }
                }
            }

            namespace MyApp
            {
                class MyService
                {
                    void DoWork()
                    {
                        Spectre.Console.AnsiConsole.Write("Hello");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoUiFrameworkInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS003");
    }

    /// <summary>Terminal.Gui usage in Tui/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS003_TerminalGuiInTui_NoDiagnostic()
    {
        const string code = """
            namespace Terminal.Gui
            {
                public class MessageBox
                {
                    public static int Query(string title, string msg) => 0;
                }
            }

            namespace MyApp
            {
                class MyScreen
                {
                    void Show()
                    {
                        Terminal.Gui.MessageBox.Query("Title", "Message");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoUiFrameworkInServicesAnalyzer>(code, "/src/PPDS.Tui/Tui/MyScreen.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Terminal.Gui in non-PPDS.Cli Services/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS003_TerminalGuiInUnrelatedServicesDir_NoDiagnostic()
    {
        const string code = """
            namespace Terminal.Gui
            {
                public class MessageBox
                {
                    public static int Query(string title, string msg) => 0;
                }
            }

            namespace MyApp
            {
                class MyService
                {
                    void DoWork()
                    {
                        Terminal.Gui.MessageBox.Query("Title", "Message");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoUiFrameworkInServicesAnalyzer>(code, "/src/OtherProject/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Normal code in Services/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS003_NormalCodeInService_NoDiagnostic()
    {
        const string code = """
            using System;
            namespace MyApp
            {
                class MyService
                {
                    void DoWork()
                    {
                        var x = Math.Max(1, 2);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoUiFrameworkInServicesAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }
}

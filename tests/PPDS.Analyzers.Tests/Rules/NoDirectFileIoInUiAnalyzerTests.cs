using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class NoDirectFileIoInUiAnalyzerTests
{
    /// <summary>File.ReadAllText() in Commands/ path should flag.</summary>
    [Fact]
    public async Task PPDS001_FileReadAllTextInCommand_ReportsWarning()
    {
        const string code = """
            using System.IO;
            class MyCommand
            {
                void Execute()
                {
                    var text = File.ReadAllText("config.json");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS001");
    }

    /// <summary>File.WriteAllText() in Tui/ path should flag.</summary>
    [Fact]
    public async Task PPDS001_FileWriteInTuiScreen_ReportsWarning()
    {
        const string code = """
            using System.IO;
            class MyScreen
            {
                void Save()
                {
                    File.WriteAllText("output.txt", "data");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/PPDS.Cli/Tui/MyScreen.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS001");
    }

    /// <summary>File.ReadAllText() in Services/ path should NOT flag.</summary>
    [Fact]
    public async Task PPDS001_FileReadInService_NoDiagnostic()
    {
        const string code = """
            using System.IO;
            class MyService
            {
                void Load()
                {
                    var text = File.ReadAllText("config.json");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>new StreamReader() in Commands/ should flag.</summary>
    [Fact]
    public async Task PPDS001_StreamReaderInCommand_ReportsWarning()
    {
        const string code = """
            using System.IO;
            class MyCommand
            {
                void Execute()
                {
                    using var reader = new StreamReader("file.txt");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS001");
    }

    /// <summary>Directory.GetFiles() in Commands/ should flag.</summary>
    [Fact]
    public async Task PPDS001_DirectoryGetFilesInCommand_ReportsWarning()
    {
        const string code = """
            using System.IO;
            class MyCommand
            {
                void Execute()
                {
                    var files = Directory.GetFiles(".");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS001");
    }

    /// <summary>Non-IO code in Commands/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS001_NonFileIoInCommand_NoDiagnostic()
    {
        const string code = """
            using System;
            class MyCommand
            {
                void Execute()
                {
                    var x = Math.Max(1, 2);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>File I/O in non-PPDS.Cli project Commands/ should NOT flag (path scoping).</summary>
    [Fact]
    public async Task PPDS001_FileIoInNonPpdsCommands_NoDiagnostic()
    {
        const string code = """
            using System.IO;
            class MyCommand
            {
                void Execute()
                {
                    var text = File.ReadAllText("config.json");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoDirectFileIoInUiAnalyzer>(code, "/src/OtherProject/Commands/MyCommand.cs");

        diagnostics.Should().BeEmpty("only PPDS.Cli presentation paths should trigger the analyzer");
    }
}

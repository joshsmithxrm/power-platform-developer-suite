using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class NoSdkInPresentationAnalyzerTests
{
    /// <summary>new ServiceClient() in Commands/ should flag.</summary>
    [Fact]
    public async Task PPDS005_ServiceClientInCommand_ReportsWarning()
    {
        const string code = """
            namespace Microsoft.PowerPlatform.Dataverse.Client
            {
                public class ServiceClient
                {
                    public ServiceClient(string conn) { }
                }
            }

            namespace MyApp
            {
                class MyCommand
                {
                    void Execute()
                    {
                        var client = new Microsoft.PowerPlatform.Dataverse.Client.ServiceClient("conn");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSdkInPresentationAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS005");
    }

    /// <summary>Using IOrganizationService in Tui/ should flag.</summary>
    [Fact]
    public async Task PPDS005_IOrganizationServiceInTui_ReportsWarning()
    {
        const string code = """
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationService
                {
                    void Execute();
                }
            }

            namespace MyApp
            {
                class MyScreen
                {
                    Microsoft.Xrm.Sdk.IOrganizationService _svc;

                    void DoWork()
                    {
                        _svc.Execute();
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSdkInPresentationAnalyzer>(code, "/src/PPDS.Cli/Tui/MyScreen.cs");

        diagnostics.Should().NotBeEmpty();
        diagnostics.Should().AllSatisfy(d => d.Id.Should().Be("PPDS005"));
    }

    /// <summary>ServiceClient in Services/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS005_ServiceClientInService_NoDiagnostic()
    {
        const string code = """
            namespace Microsoft.PowerPlatform.Dataverse.Client
            {
                public class ServiceClient
                {
                    public ServiceClient(string conn) { }
                }
            }

            namespace MyApp
            {
                class MyService
                {
                    void DoWork()
                    {
                        var client = new Microsoft.PowerPlatform.Dataverse.Client.ServiceClient("conn");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSdkInPresentationAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>ServiceClient in Commands/Serve/ should NOT flag (MCP exception).</summary>
    [Fact]
    public async Task PPDS005_ServiceClientInServeCommand_NoDiagnostic()
    {
        const string code = """
            namespace Microsoft.PowerPlatform.Dataverse.Client
            {
                public class ServiceClient
                {
                    public ServiceClient(string conn) { }
                }
            }

            namespace MyApp
            {
                class ServeCommand
                {
                    void Execute()
                    {
                        var client = new Microsoft.PowerPlatform.Dataverse.Client.ServiceClient("conn");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSdkInPresentationAnalyzer>(code, "/src/PPDS.Cli/Commands/Serve/ServeCommand.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>Using application service types in Commands/ should NOT flag.</summary>
    [Fact]
    public async Task PPDS005_ApplicationServiceInCommand_NoDiagnostic()
    {
        const string code = """
            using System;
            namespace MyApp
            {
                class ImportService
                {
                    public void Run() { }
                }

                class MyCommand
                {
                    void Execute()
                    {
                        var svc = new ImportService();
                        svc.Run();
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSdkInPresentationAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>SDK types in non-PPDS.Cli project Commands/ should NOT flag (path scoping).</summary>
    [Fact]
    public async Task PPDS005_SdkInNonPpdsCommands_NoDiagnostic()
    {
        const string code = """
            namespace Microsoft.PowerPlatform.Dataverse.Client
            {
                public class ServiceClient
                {
                    public ServiceClient(string conn) { }
                }
            }

            namespace MyApp
            {
                class MyCommand
                {
                    void Execute()
                    {
                        var client = new Microsoft.PowerPlatform.Dataverse.Client.ServiceClient("conn");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSdkInPresentationAnalyzer>(code, "/src/OtherProject/Commands/MyCommand.cs");

        diagnostics.Should().BeEmpty("only PPDS.Cli presentation paths should trigger the analyzer");
    }
}

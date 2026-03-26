using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class UseEarlyBoundEntitiesAnalyzerTests
{
    /// <summary>PPDS006: String literal in QueryExpression ctor should flag.</summary>
    [Fact]
    public async Task PPDS006_StringLiteralInQueryExpression_ReportsWarning()
    {
        const string code = """
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression() { }
                    public QueryExpression(string entityName) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk.Query;
                class MyService
                {
                    void DoWork()
                    {
                        var query = new QueryExpression("account");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseEarlyBoundEntitiesAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS006");
    }

    /// <summary>PPDS006: EntityLogicalName constant should NOT flag.</summary>
    [Fact]
    public async Task PPDS006_EntityLogicalNameConstant_NoDiagnostic()
    {
        const string code = """
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression() { }
                    public QueryExpression(string entityName) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk.Query;
                class Account
                {
                    public const string EntityLogicalName = "account";
                }
                class MyService
                {
                    void DoWork()
                    {
                        var query = new QueryExpression(Account.EntityLogicalName);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseEarlyBoundEntitiesAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS006: Variable argument should NOT flag.</summary>
    [Fact]
    public async Task PPDS006_VariableArgument_NoDiagnostic()
    {
        const string code = """
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression() { }
                    public QueryExpression(string entityName) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk.Query;
                class MyService
                {
                    void DoWork(string entityVar)
                    {
                        var query = new QueryExpression(entityVar);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseEarlyBoundEntitiesAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS006: Diagnostic message should suggest the correct early-bound class.</summary>
    [Fact]
    public async Task PPDS006_MessageContainsSuggestedClass()
    {
        const string code = """
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression() { }
                    public QueryExpression(string entityName) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk.Query;
                class MyService
                {
                    void DoWork()
                    {
                        var query = new QueryExpression("pluginassembly");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseEarlyBoundEntitiesAnalyzer>(code);

        diagnostics.Should().ContainSingle();
        var message = diagnostics[0].GetMessage();
        message.Should().Contain("PluginAssembly");
        message.Should().Contain("pluginassembly");
    }

    /// <summary>PPDS006: No-arg constructor should NOT flag.</summary>
    [Fact]
    public async Task PPDS006_NoArguments_NoDiagnostic()
    {
        const string code = """
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression() { }
                    public QueryExpression(string entityName) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk.Query;
                class MyService
                {
                    void DoWork()
                    {
                        var query = new QueryExpression();
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseEarlyBoundEntitiesAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }
}

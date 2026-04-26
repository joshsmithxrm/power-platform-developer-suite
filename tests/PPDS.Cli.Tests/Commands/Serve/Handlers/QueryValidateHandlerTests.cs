using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Behavioral unit tests for the query/validate RPC method (DM-01 daemon side, DM-08).
/// Exercises the actual handler code path via <see cref="RpcMethodHandler.QueryValidateAsync"/>
/// — no source-text matching, no reflection-on-source.
/// </summary>
[Trait("Category", "Unit")]
public class QueryValidateHandlerTests
{
    private static RpcMethodHandler CreateHandler()
    {
        var mockPoolManager = new Mock<IDaemonConnectionPoolManager>();
        var authServices = new ServiceCollection().AddAuthServices().BuildServiceProvider();
        return new RpcMethodHandler(mockPoolManager.Object, authServices);
    }

    [Fact]
    public async Task QueryValidate_ValidSql_ReturnsEmptyDiagnostics()
    {
        var handler = CreateHandler();
        var request = new QueryValidateRequest { Sql = "SELECT name FROM account", Language = "sql" };

        var response = await handler.QueryValidateAsync(request);

        Assert.NotNull(response);
        Assert.NotNull(response.Diagnostics);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task QueryValidate_InvalidSql_ReturnsErrorDiagnostics()
    {
        var handler = CreateHandler();
        // "FORM" is a typo for "FROM" — ScriptDom reports a parse error.
        var request = new QueryValidateRequest { Sql = "SELECT name FORM account", Language = "sql" };

        var response = await handler.QueryValidateAsync(request);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Diagnostics);
        var diag = response.Diagnostics[0];
        Assert.Equal("error", diag.Severity);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
        Assert.True(diag.Length >= 1);
        Assert.True(diag.Start >= 0);
    }

    /// <summary>
    /// DM-08: FetchXML validation is a non-goal — language="xml" must short-circuit
    /// regardless of whether the SQL would otherwise parse.
    /// </summary>
    [Fact]
    public async Task QueryValidate_FetchXmlLanguage_ReturnsEmptyDiagnostics()
    {
        var handler = CreateHandler();
        // Even malformed XML / non-SQL content must be ignored when language=="xml".
        var request = new QueryValidateRequest
        {
            Sql = "<fetch><entity name='account'><attribute name='name'/></entity></fetch>",
            Language = "xml",
        };

        var response = await handler.QueryValidateAsync(request);

        Assert.NotNull(response);
        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task QueryValidate_FetchXmlLanguage_CaseInsensitive_ReturnsEmpty()
    {
        var handler = CreateHandler();
        var request = new QueryValidateRequest
        {
            Sql = "SELECT FORM account",  // would be a parse error if treated as SQL
            Language = "XML",
        };

        var response = await handler.QueryValidateAsync(request);

        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task QueryValidate_EmptySql_ReturnsEmptyDiagnostics()
    {
        var handler = CreateHandler();
        var request = new QueryValidateRequest { Sql = "", Language = "sql" };

        var response = await handler.QueryValidateAsync(request);

        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task QueryValidate_WhitespaceOnly_ReturnsEmptyDiagnostics()
    {
        var handler = CreateHandler();
        var request = new QueryValidateRequest { Sql = "   \n\t  ", Language = "sql" };

        var response = await handler.QueryValidateAsync(request);

        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task QueryValidate_ShortSql_ReturnsEmptyDiagnostics()
    {
        var handler = CreateHandler();
        // Less than 3 characters after trim — should short-circuit.
        var request = new QueryValidateRequest { Sql = "SE", Language = "sql" };

        var response = await handler.QueryValidateAsync(request);

        Assert.Empty(response.Diagnostics);
    }

    [Fact]
    public async Task QueryValidate_NullLanguage_TreatedAsSql()
    {
        var handler = CreateHandler();
        var request = new QueryValidateRequest { Sql = "SELECT name FROM account", Language = null };

        var response = await handler.QueryValidateAsync(request);

        Assert.NotNull(response);
        Assert.Empty(response.Diagnostics);
    }
}

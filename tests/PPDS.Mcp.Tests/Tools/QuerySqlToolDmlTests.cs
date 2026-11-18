using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// The MCP SQL tool only transpiles to a FetchXML read: an INSERT, UPDATE or DELETE never reaches Dataverse through it,
/// in a read-only session or not. Strict mocks fail the test on any call to the query or bulk executor.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QuerySqlToolDmlTests
{
    [Theory]
    [InlineData("DELETE FROM account WHERE name = 'x'", false)]
    [InlineData("UPDATE account SET name = 'y' WHERE name = 'x'", false)]
    [InlineData("INSERT INTO account (name) VALUES ('x')", false)]
    [InlineData("DELETE FROM account WHERE name = 'x'", true)]
    public async Task Dml_NeverExecutes_ThroughTheMcpQueryTool(string sql, bool readOnly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppds-mcp-dml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
            var executor = new Mock<IQueryExecutor>(MockBehavior.Strict);
            var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Strict);
            using var context = new McpToolContext(
                new Mock<IMcpConnectionPoolManager>(MockBehavior.Strict).Object, store,
                new Mock<ISecureCredentialStore>(MockBehavior.Strict).Object, new McpSessionOptions { ReadOnly = readOnly })
            {
                ServiceProviderFactory = (_, _, _, _) => new ServiceCollection()
                    .AddSingleton(executor.Object).AddSingleton(bulk.Object).BuildServiceProvider()
            };
            await store.SaveAsync(new ProfileCollection
            {
                ActiveProfileIndex = 1,
                Profiles = [new AuthProfile { Index = 1, Name = "fixture", Environment = new() { Url = "https://fixture.example.test" } }]
            });
            await context.InitializeAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => new QuerySqlTool(context).ExecuteAsync(sql));

            executor.VerifyNoOtherCalls();
            bulk.VerifyNoOtherCalls();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public sealed class PluginDiffReadCoverageTests
{
    private static (PluginRegistrationService Service, Mock<IPooledClient> Client) Fixture()
    {
        var client = new Mock<IPooledClient>(MockBehavior.Loose);
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return (new PluginRegistrationService(pool.Object, new InactiveFakeShakedownGuard(),
            NullLogger<PluginRegistrationService>.Instance), client);
    }

    [Fact]
    public async Task ListReads_AllManagedNonsecretColumns_MapsValuesAndPages()
    {
        var f = Fixture();
        var userId = Guid.NewGuid();
        var row = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "handler", ["stage"] = new OptionSetValue(40), ["mode"] = new OptionSetValue(1),
            ["rank"] = 2, ["filteringattributes"] = "name", ["configuration"] = "payload",
            ["statecode"] = new OptionSetValue(1), ["description"] = "step description",
            ["supporteddeployment"] = new OptionSetValue(2),
            ["impersonatinguserid"] = new EntityReference("systemuser", userId),
            ["asyncautodelete"] = true, ["canbebypassed"] = false, ["canusereadonlyconnection"] = true,
            ["invocationsource"] = new OptionSetValue(-1), ["ishidden"] = new BooleanManagedProperty(true)
        };
        row["message.name"] = new AliasedValue("sdkmessage", "name", "Update");
        var image = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"] = "before", ["imagetype"] = new OptionSetValue(0), ["entityalias"] = "alias",
            ["attributes"] = "name", ["messagepropertyname"] = "Target", ["description"] = "image description"
        };
        var requests = new List<(string Table, int Page, string[] Columns, string[] Filters)>();
        using var cts = new CancellationTokenSource();
        f.Client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), cts.Token))
            .Returns<QueryBase, CancellationToken>((query, _) =>
            {
                var q = (QueryExpression)query;
                requests.Add((q.EntityName, q.PageInfo.PageNumber, q.ColumnSet.Columns.ToArray(),
                    q.Criteria.Conditions.Select(c => c.AttributeName).ToArray()));
                return Task.FromResult(q.EntityName == "sdkmessageprocessingstepimage"
                    ? new EntityCollection([image])
                    : q.PageInfo.PageNumber == 1
                        ? new EntityCollection([row]) { MoreRecords = true, PagingCookie = "cookie" }
                        : new EntityCollection([new Entity("sdkmessageprocessingstep", Guid.NewGuid())]));
            });
        var steps = await f.Service.ListStepsForTypeAsync(Guid.NewGuid(), new PluginListOptions(IncludeHidden: true), cts.Token);
        var images = await f.Service.ListImagesForStepAsync(row.Id, cts.Token);
        Assert.Equal(2, steps.Count);
        var step = steps[0];
        Assert.False(step.IsEnabled);
        Assert.Equal("Both", step.Deployment);
        Assert.Equal(userId, step.ImpersonatingUserId);
        Assert.Equal("payload", step.Configuration);
        Assert.Equal("step description", step.Description);
        Assert.True(step.AsyncAutoDelete);
        Assert.False(step.CanBeBypassed);
        Assert.True(step.CanUseReadOnlyConnection);
        Assert.Equal("Internal", step.InvocationSource);
        Assert.True(step.IsHidden);
        Assert.Empty(step.UnavailableProperties);
        var resultImage = Assert.Single(images);
        Assert.Equal("image description", resultImage.Description);
        Assert.Equal("Target", resultImage.MessagePropertyName);
        Assert.Equal("alias", resultImage.EntityAlias);
        Assert.Empty(resultImage.UnavailableProperties);
        Assert.Equal(new[] { 1, 2 }, requests.Where(r => r.Table == "sdkmessageprocessingstep").Select(r => r.Page));
        var columns = requests[0].Columns;
        foreach (var field in new[] { "canbebypassed", "canusereadonlyconnection", "invocationsource", "ishidden",
            "configuration", "description", "statecode", "supporteddeployment", "impersonatinguserid", "asyncautodelete" })
            Assert.Contains(field, columns);
        Assert.DoesNotContain("sdkmessageprocessingstepsecureconfigid", columns);
        Assert.DoesNotContain("secureconfig", columns);
        Assert.DoesNotContain("ishidden", requests[0].Filters);
        Assert.Contains("description", requests[2].Columns);
        Assert.Contains("messagepropertyname", requests[2].Columns);
    }

    [Fact]
    public async Task MissingNonnullableReadValues_AreUnavailable_NotAssumedDefaults()
    {
        var f = Fixture();
        f.Client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection([new Entity("sdkmessageprocessingstep", Guid.NewGuid())]));
        var result = Assert.Single(await f.Service.ListStepsForTypeAsync(Guid.NewGuid()));
        foreach (var name in new[] { "name", "stage", "mode", "executionOrder", "enabled", "deployment",
            "asyncAutoDelete", "canBeBypassed", "canUseReadOnlyConnection", "invocationSource", "isHidden" })
            Assert.Contains(name, result.UnavailableProperties);
        // Dataverse omits nullable text/lookup attributes to represent null; those are legitimate clears.
        Assert.DoesNotContain("description", result.UnavailableProperties);
        Assert.DoesNotContain("unsecureConfiguration", result.UnavailableProperties);
        Assert.DoesNotContain("runAsUser", result.UnavailableProperties);
    }

    [Fact]
    public async Task LegacyList_DefaultFilterStillExcludesHidden()
    {
        var f = Fixture();
        QueryExpression? request = null;
        f.Client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => request = (QueryExpression)q)
            .ReturnsAsync(new EntityCollection());
        await f.Service.ListStepsForTypeAsync(Guid.NewGuid());
        Assert.NotNull(request);
        Assert.Contains(request.Criteria.Conditions, c => c.AttributeName == "ishidden");
    }
}

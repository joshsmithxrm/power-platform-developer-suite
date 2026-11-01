using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.WebResources;

public class WebResourcePrefixTests
{
    [Theory]
    [InlineData("script.js", null, null, "WebResource.MissingPublisherPrefix")]
    [InlineData("folder/new_script.js", null, null, "WebResource.MissingPublisherPrefix")]
    [InlineData("_script.js", null, null, "WebResource.MissingPublisherPrefix")]
    [InlineData("new_script.js", null, null, null)]
    [InlineData("new_/icons/icon.svg", null, null, null)]
    [InlineData("new_script.js", "Example", "new", null)]
    [InlineData("old_script.js", "Example", "new", "WebResource.PublisherPrefixMismatch")]
    public async Task Create_PrefixWarningsPreserveSubmittedName(string name, string? solution, string? expectedPrefix, string? expectedCode)
    {
        var client = new Mock<IPooledClient>();
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        var row = new Entity("solution", Guid.NewGuid());
        row["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", expectedPrefix);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken _) => ((QueryExpression)q).EntityName == "solution"
                ? new EntityCollection([row]) : new EntityCollection());
        OrganizationRequest? sent = null;
        var response = new CreateResponse();
        response.Results["id"] = Guid.NewGuid();
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((r, _) => sent = r).ReturnsAsync(response);
        var service = new WebResourceService(pool.Object, Mock.Of<ISolutionService>(),
            new InactiveFakeShakedownGuard(), NullLogger<WebResourceService>.Instance);

        var result = await service.CreateAsync(new(name, null, 3, [1], solution));

        Assert.Equal(name, Assert.IsType<CreateRequest>(sent).Target["name"]);
        if (expectedCode is null) Assert.Empty(result.Warnings);
        else Assert.Equal(expectedCode, Assert.Single(result.Warnings).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_MissingSolutionOrPublisher_FailsBeforeWrite(bool solutionExists)
    {
        var client = new Mock<IPooledClient>();
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken _) => solutionExists && ((QueryExpression)q).EntityName == "solution"
                ? new EntityCollection([new("solution", Guid.NewGuid())]) : new EntityCollection());
        var service = new WebResourceService(pool.Object, Mock.Of<ISolutionService>(),
            new InactiveFakeShakedownGuard(), NullLogger<WebResourceService>.Instance);

        var error = await Assert.ThrowsAsync<PpdsException>(() => service.CreateAsync(new("new_script.js", null, 3, [1], "Example")));

        Assert.Equal(ErrorCodes.Operation.NotFound, error.ErrorCode);
        client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

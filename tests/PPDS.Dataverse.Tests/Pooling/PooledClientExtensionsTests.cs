using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Pooling;

public class PooledClientExtensionsTests
{
    private readonly Mock<IPooledClient> _client = new();

    #region RetrieveUnpublishedAsync Tests

    [Fact]
    public async Task RetrieveUnpublishedAsync_ExecutesCorrectRequest()
    {
        // Arrange
        var entityLogicalName = "webresource";
        var id = Guid.NewGuid();
        var columnSet = new ColumnSet("content", "name");
        var expectedEntity = new Entity(entityLogicalName, id);

        OrganizationRequest? capturedRequest = null;
        _client
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new RetrieveUnpublishedResponse
            {
                Results = new ParameterCollection
                {
                    { "Entity", expectedEntity }
                }
            });

        // Act
        var result = await _client.Object.RetrieveUnpublishedAsync(
            entityLogicalName, id, columnSet, CancellationToken.None);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest.Should().BeOfType<RetrieveUnpublishedRequest>();

        var request = (RetrieveUnpublishedRequest)capturedRequest!;
        request.Target.LogicalName.Should().Be(entityLogicalName);
        request.Target.Id.Should().Be(id);
        request.ColumnSet.Should().BeSameAs(columnSet);

        result.Should().BeSameAs(expectedEntity);
    }

    #endregion

    #region PublishXmlAsync Tests

    [Fact]
    public async Task PublishXmlAsync_ExecutesRequest()
    {
        // Arrange — use unique key to avoid interference with other tests
        var envKey = $"publish-xml-{Guid.NewGuid()}";
        var parameterXml = "<importexportxml><webresources><webresource>{abc}</webresource></webresources></importexportxml>";

        OrganizationRequest? capturedRequest = null;
        _client
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new PublishXmlResponse());

        // Act
        await _client.Object.PublishXmlAsync(parameterXml, envKey);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest.Should().BeOfType<PublishXmlRequest>();

        var request = (PublishXmlRequest)capturedRequest!;
        request.ParameterXml.Should().Be(parameterXml);
    }

    [Fact]
    public async Task PublishXmlAsync_ThrowsWhenConcurrentPublish()
    {
        // Arrange — use unique key to avoid interference with other tests
        var envKey = $"concurrent-publish-{Guid.NewGuid()}";
        var tcs = new TaskCompletionSource<OrganizationResponse>();

        _client
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        // Act — start a publish that won't complete (held by tcs)
        var firstPublish = _client.Object.PublishXmlAsync("<xml/>", envKey);

        // The second publish should throw immediately
        var act = () => _client.Object.PublishXmlAsync("<xml/>", envKey);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in progress*");

        // Cleanup — let the first publish complete
        tcs.SetResult(new PublishXmlResponse());
        await firstPublish;
    }

    [Fact]
    public async Task PublishXmlAsync_ReleasesLockAfterCompletion()
    {
        // Arrange — use unique key to avoid interference with other tests
        var envKey = $"release-lock-{Guid.NewGuid()}";

        _client
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishXmlResponse());

        // Act — first publish completes
        await _client.Object.PublishXmlAsync("<xml/>", envKey);

        // Second publish should succeed (lock was released)
        var act = () => _client.Object.PublishXmlAsync("<xml/>", envKey);

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region PublishAllXmlAsync Tests

    [Fact]
    public async Task PublishAllXmlAsync_SharesLockWithPublishXml()
    {
        // Arrange — use unique key to avoid interference with other tests
        var envKey = $"shared-lock-{Guid.NewGuid()}";
        var tcs = new TaskCompletionSource<OrganizationResponse>();

        _client
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        // Act — start a PublishXml that won't complete (held by tcs)
        var publishXmlTask = _client.Object.PublishXmlAsync("<xml/>", envKey);

        // PublishAllXml should throw because the shared lock is held
        var act = () => _client.Object.PublishAllXmlAsync(envKey);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in progress*");

        // Cleanup
        tcs.SetResult(new PublishAllXmlResponse());
        await publishXmlTask;
    }

    #endregion
}

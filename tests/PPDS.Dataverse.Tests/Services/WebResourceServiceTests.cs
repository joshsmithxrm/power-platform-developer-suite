using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Dataverse.Tests.Services;

public class WebResourceServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly Mock<ISolutionService> _solutionService = new();
    private readonly NullLogger<WebResourceService> _logger = new();

    public WebResourceServiceTests()
    {
        _pool
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
    }

    private WebResourceService CreateService() =>
        new(_pool.Object, _solutionService.Object, _logger);

    #region ListAsync Tests

    [Fact]
    public async Task ListAsync_ReturnsAllResources_WhenNoFilter()
    {
        // Arrange
        var entities = new EntityCollection(new List<Entity>
        {
            CreateWebResourceEntity(Guid.NewGuid(), "new_script.js", 3),
            CreateWebResourceEntity(Guid.NewGuid(), "new_style.css", 2)
        });

        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        var service = CreateService();

        // Act
        var result = await service.ListAsync();

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items[0].Name.Should().Be("new_script.js");
        result.Items[1].Name.Should().Be("new_style.css");

        // Verify no solution service was called
        _solutionService.Verify(
            s => s.GetComponentsAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListAsync_FiltersTextOnly_WhenTextOnlyTrue()
    {
        // Arrange
        QueryExpression? capturedQuery = null;
        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => capturedQuery = q as QueryExpression)
            .ReturnsAsync(new EntityCollection());

        var service = CreateService();

        // Act
        await service.ListAsync(textOnly: true);

        // Assert
        capturedQuery.Should().NotBeNull();
        capturedQuery!.Criteria.Conditions.Should().ContainSingle(c =>
            c.AttributeName == WebResource.Fields.WebResourceType &&
            c.Operator == ConditionOperator.In);

        var typeCondition = capturedQuery.Criteria.Conditions
            .First(c => c.AttributeName == WebResource.Fields.WebResourceType);

        // Text types: 1 (HTML), 2 (CSS), 3 (JS), 4 (XML), 9 (XSL), 11 (SVG), 12 (RESX)
        var typeValues = typeCondition.Values.Cast<object>().Select(v => (int)v).ToList();
        typeValues.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 9, 11, 12 });
    }

    [Fact]
    public async Task ListAsync_FiltersBySolution_WhenSolutionIdProvided()
    {
        // Arrange
        var solutionId = Guid.NewGuid();
        var wrId1 = Guid.NewGuid();
        var wrId2 = Guid.NewGuid();

        _solutionService
            .Setup(s => s.GetComponentsAsync(solutionId, 61, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SolutionComponentInfo>
            {
                new(Guid.NewGuid(), wrId1, 61, "WebResource", 0, false),
                new(Guid.NewGuid(), wrId2, 61, "WebResource", 0, false)
            });

        QueryExpression? capturedQuery = null;
        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => capturedQuery = q as QueryExpression)
            .ReturnsAsync(new EntityCollection());

        var service = CreateService();

        // Act
        await service.ListAsync(solutionId: solutionId);

        // Assert
        _solutionService.Verify(
            s => s.GetComponentsAsync(solutionId, 61, It.IsAny<CancellationToken>()),
            Times.Once);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.Criteria.Conditions.Should().ContainSingle(c =>
            c.AttributeName == WebResource.Fields.WebResourceId &&
            c.Operator == ConditionOperator.In);

        var idCondition = capturedQuery.Criteria.Conditions
            .First(c => c.AttributeName == WebResource.Fields.WebResourceId);
        var idValues = idCondition.Values.Cast<object>().Select(v => (Guid)v).ToList();
        idValues.Should().BeEquivalentTo(new[] { wrId1, wrId2 });
    }

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenSolutionHasNoWebResources()
    {
        // Arrange
        var solutionId = Guid.NewGuid();

        _solutionService
            .Setup(s => s.GetComponentsAsync(solutionId, 61, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SolutionComponentInfo>());

        var service = CreateService();

        // Act
        var result = await service.ListAsync(solutionId: solutionId);

        // Assert
        result.Items.Should().BeEmpty();

        // Should not even query Dataverse when there are no component IDs
        _client.Verify(
            c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region GetContentAsync Tests

    [Fact]
    public async Task GetContentAsync_DecodesBase64Content()
    {
        // Arrange
        var id = Guid.NewGuid();
        var originalContent = "function hello() { return 'world'; }";
        var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(originalContent));

        var entity = new Entity(WebResource.EntityLogicalName, id);
        entity[WebResource.Fields.Content] = base64Content;
        entity[WebResource.Fields.Name] = "new_hello.js";
        entity[WebResource.Fields.WebResourceType] = new OptionSetValue(3);
        entity[WebResource.Fields.ModifiedOn] = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);

        // For published=true path, uses RetrieveMultiple
        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { entity }));

        var service = CreateService();

        // Act
        var result = await service.GetContentAsync(id, published: true);

        // Assert
        result.Should().NotBeNull();
        result!.Content.Should().Be(originalContent);
        result.Name.Should().Be("new_hello.js");
        result.WebResourceType.Should().Be(3);
        result.ModifiedOn.Should().Be(new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GetContentAsync_ReturnsNull_WhenNoContent()
    {
        // Arrange
        var id = Guid.NewGuid();

        var entity = new Entity(WebResource.EntityLogicalName, id);
        // Content field is not set (null)
        entity[WebResource.Fields.Name] = "new_empty.js";
        entity[WebResource.Fields.WebResourceType] = new OptionSetValue(3);

        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { entity }));

        var service = CreateService();

        // Act
        var result = await service.GetContentAsync(id, published: true);

        // Assert
        result.Should().NotBeNull();
        result!.Content.Should().BeNull();
    }

    #endregion

    #region GetModifiedOnAsync Tests

    [Fact]
    public async Task GetModifiedOnAsync_ReturnsTimestamp_WhenFound()
    {
        // Arrange
        var id = Guid.NewGuid();
        var expectedTimestamp = new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc);

        var entity = new Entity(WebResource.EntityLogicalName, id);
        entity[WebResource.Fields.ModifiedOn] = expectedTimestamp;

        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { entity }));

        var service = CreateService();

        // Act
        var result = await service.GetModifiedOnAsync(id);

        // Assert
        result.Should().Be(expectedTimestamp);
    }

    [Fact]
    public async Task GetModifiedOnAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        var id = Guid.NewGuid();

        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var service = CreateService();

        // Act
        var result = await service.GetModifiedOnAsync(id);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region UpdateContentAsync Tests

    [Fact]
    public async Task UpdateContentAsync_ThrowsForBinaryTypes()
    {
        // Arrange — PNG is type 5, which is not a text type
        var id = Guid.NewGuid();
        var pngEntity = CreateWebResourceEntity(id, "new_image.png", 5);

        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { pngEntity }));

        var service = CreateService();

        // Act
        var act = () => service.UpdateContentAsync(id, "some content");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be edited*");
    }

    [Fact]
    public async Task UpdateContentAsync_EncodesAndSaves()
    {
        // Arrange
        var id = Guid.NewGuid();
        var jsEntity = CreateWebResourceEntity(id, "new_script.js", 3);
        var content = "console.log('hello');";
        var expectedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

        // GetAsync (called internally) uses RetrieveMultiple
        _client
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { jsEntity }));

        Entity? capturedUpdate = null;
        _client
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedUpdate = e)
            .Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        await service.UpdateContentAsync(id, content);

        // Assert
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.LogicalName.Should().Be(WebResource.EntityLogicalName);
        capturedUpdate.Id.Should().Be(id);
        capturedUpdate[WebResource.Fields.Content].Should().Be(expectedBase64);
    }

    #endregion

    #region WebResourceInfo Computed Properties

    [Theory]
    [InlineData(1, "HTML")]
    [InlineData(2, "CSS")]
    [InlineData(3, "JavaScript")]
    [InlineData(4, "XML")]
    [InlineData(5, "PNG")]
    [InlineData(6, "JPG")]
    [InlineData(7, "GIF")]
    [InlineData(8, "XAP (Silverlight)")]
    [InlineData(9, "XSL")]
    [InlineData(10, "ICO")]
    [InlineData(11, "SVG")]
    [InlineData(12, "RESX")]
    [InlineData(99, "Unknown (99)")]
    public void WebResourceInfo_TypeName_MapsCorrectly(int typeCode, string expectedName)
    {
        var info = new WebResourceInfo(
            Guid.NewGuid(), "test", null, typeCode, false, null, null, null, null);

        info.TypeName.Should().Be(expectedName);
    }

    [Theory]
    [InlineData(1, "html")]
    [InlineData(2, "css")]
    [InlineData(3, "js")]
    [InlineData(4, "xml")]
    [InlineData(5, "png")]
    [InlineData(6, "jpg")]
    [InlineData(7, "gif")]
    [InlineData(8, "xap")]
    [InlineData(9, "xsl")]
    [InlineData(10, "ico")]
    [InlineData(11, "svg")]
    [InlineData(12, "resx")]
    [InlineData(99, "bin")]
    public void WebResourceInfo_FileExtension_MapsCorrectly(int typeCode, string expectedExtension)
    {
        var info = new WebResourceInfo(
            Guid.NewGuid(), "test", null, typeCode, false, null, null, null, null);

        info.FileExtension.Should().Be(expectedExtension);
    }

    [Theory]
    [InlineData(1, true)]   // HTML
    [InlineData(2, true)]   // CSS
    [InlineData(3, true)]   // JavaScript
    [InlineData(4, true)]   // XML
    [InlineData(5, false)]  // PNG
    [InlineData(6, false)]  // JPG
    [InlineData(7, false)]  // GIF
    [InlineData(8, false)]  // XAP
    [InlineData(9, true)]   // XSL
    [InlineData(10, false)] // ICO
    [InlineData(11, true)]  // SVG
    [InlineData(12, true)]  // RESX
    public void WebResourceInfo_IsTextType_CorrectForAllTypes(int typeCode, bool expectedIsText)
    {
        var info = new WebResourceInfo(
            Guid.NewGuid(), "test", null, typeCode, false, null, null, null, null);

        info.IsTextType.Should().Be(expectedIsText);
    }

    #endregion

    #region Helpers

    private static Entity CreateWebResourceEntity(Guid id, string name, int typeCode)
    {
        var entity = new Entity(WebResource.EntityLogicalName, id);
        entity[WebResource.Fields.Name] = name;
        entity[WebResource.Fields.WebResourceType] = new OptionSetValue(typeCode);
        entity[WebResource.Fields.IsManaged] = false;
        return entity;
    }

    #endregion
}

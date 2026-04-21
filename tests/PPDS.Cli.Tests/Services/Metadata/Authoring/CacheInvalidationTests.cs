using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Verifies that <see cref="DataverseMetadataAuthoringService"/> invalidates the
/// <see cref="ICachedMetadataProvider"/> after successful authoring operations,
/// and does NOT invalidate on dry-run.
/// </summary>
[Trait("Category", "Unit")]
public class CacheInvalidationTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly Mock<ICachedMetadataProvider> _cache = new();
    private readonly SchemaValidator _validator = new();
    private readonly DataverseMetadataAuthoringService _service;

    public CacheInvalidationTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        SetupPublisherPrefixQuery("new");

        _service = new DataverseMetadataAuthoringService(
            _pool.Object,
            _validator,
            new InactiveFakeShakedownGuard(),
            logger: null,
            cacheProvider: _cache.Object);
    }

    private void SetupPublisherPrefixQuery(string prefix)
    {
        var publisherId = Guid.NewGuid();

        var solutionEntity = new Entity("solution")
        {
            ["publisherid"] = new EntityReference("publisher", publisherId)
        };
        var solutionCollection = new EntityCollection(new System.Collections.Generic.List<Entity> { solutionEntity });

        var publisherEntity = new Entity("publisher")
        {
            ["customizationprefix"] = prefix
        };
        var publisherCollection = new EntityCollection(new System.Collections.Generic.List<Entity> { publisherEntity });

        _client.SetupSequence(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(solutionCollection)
            .ReturnsAsync(publisherCollection);
    }

    #region Table Operations

    [Fact]
    public async Task CreateTable_InvalidatesEntityList()
    {
        var response = new CreateEntityResponse();
        response.Results["EntityId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables",
            OwnershipType = "UserOwned"
        };

        await _service.CreateTableAsync(request);

        _cache.Verify(c => c.InvalidateEntityList(), Times.Once);
        _cache.Verify(c => c.InvalidateEntity("new_testtable"), Times.Once);
    }

    [Fact]
    public async Task DeleteTable_InvalidatesEntityList()
    {
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        var request = new DeleteTableRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account"
        };

        await _service.DeleteTableAsync(request);

        _cache.Verify(c => c.InvalidateEntityList(), Times.Once);
        _cache.Verify(c => c.InvalidateEntity("account"), Times.Once);
    }

    #endregion

    #region Column Operations

    [Fact]
    public async Task CreateColumn_InvalidatesEntityCache_Scoped()
    {
        var response = new CreateAttributeResponse();
        response.Results["AttributeId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            SchemaName = "new_TestColumn",
            DisplayName = "Test Column",
            ColumnType = SchemaColumnType.String
        };

        await _service.CreateColumnAsync(request);

        _cache.Verify(c => c.InvalidateEntity("account"), Times.Once);
        // Entity list should NOT be invalidated for column operations
        _cache.Verify(c => c.InvalidateEntityList(), Times.Never);
    }

    [Fact]
    public async Task CreateColumn_OnAccount_DoesNotInvalidateContact()
    {
        var response = new CreateAttributeResponse();
        response.Results["AttributeId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            SchemaName = "new_TestColumn",
            DisplayName = "Test Column",
            ColumnType = SchemaColumnType.String
        };

        await _service.CreateColumnAsync(request);

        _cache.Verify(c => c.InvalidateEntity("account"), Times.Once);
        _cache.Verify(c => c.InvalidateEntity("contact"), Times.Never);
    }

    #endregion

    #region Global Choice Operations

    [Fact]
    public async Task CreateGlobalChoice_InvalidatesGlobalOptionSets()
    {
        var response = new CreateOptionSetResponse();
        response.Results["OptionSetId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateGlobalChoiceRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestChoice",
            DisplayName = "Test Choice"
        };

        await _service.CreateGlobalChoiceAsync(request);

        _cache.Verify(c => c.InvalidateGlobalOptionSets(), Times.Once);
    }

    [Fact]
    public async Task DeleteGlobalChoice_InvalidatesGlobalOptionSets()
    {
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        var request = new DeleteGlobalChoiceRequest
        {
            SolutionUniqueName = "TestSolution",
            Name = "new_TestChoice"
        };

        await _service.DeleteGlobalChoiceAsync(request);

        _cache.Verify(c => c.InvalidateGlobalOptionSets(), Times.Once);
    }

    #endregion

    #region Dry-Run Does Not Invalidate

    [Fact]
    public async Task CreateTable_DryRun_DoesNotInvalidateCache()
    {
        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables",
            DryRun = true
        };

        await _service.CreateTableAsync(request);

        _cache.Verify(c => c.InvalidateEntityList(), Times.Never);
        _cache.Verify(c => c.InvalidateEntity(It.IsAny<string>()), Times.Never);
        _cache.Verify(c => c.InvalidateGlobalOptionSets(), Times.Never);
        _cache.Verify(c => c.InvalidateAll(), Times.Never);
    }

    [Fact]
    public async Task CreateColumn_DryRun_DoesNotInvalidateCache()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            SchemaName = "new_TestColumn",
            DisplayName = "Test Column",
            ColumnType = SchemaColumnType.String,
            DryRun = true
        };

        await _service.CreateColumnAsync(request);

        _cache.Verify(c => c.InvalidateEntity(It.IsAny<string>()), Times.Never);
        _cache.Verify(c => c.InvalidateEntityList(), Times.Never);
        _cache.Verify(c => c.InvalidateGlobalOptionSets(), Times.Never);
        _cache.Verify(c => c.InvalidateAll(), Times.Never);
    }

    [Fact]
    public async Task CreateGlobalChoice_DryRun_DoesNotInvalidateCache()
    {
        var request = new CreateGlobalChoiceRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestChoice",
            DisplayName = "Test Choice",
            DryRun = true
        };

        await _service.CreateGlobalChoiceAsync(request);

        _cache.Verify(c => c.InvalidateGlobalOptionSets(), Times.Never);
        _cache.Verify(c => c.InvalidateEntity(It.IsAny<string>()), Times.Never);
        _cache.Verify(c => c.InvalidateEntityList(), Times.Never);
        _cache.Verify(c => c.InvalidateAll(), Times.Never);
    }

    #endregion

    #region No Cache Provider (null)

    [Fact]
    public async Task CreateTable_WithoutCacheProvider_DoesNotThrow()
    {
        var serviceWithoutCache = new DataverseMetadataAuthoringService(
            _pool.Object,
            _validator,
            new InactiveFakeShakedownGuard());

        var response = new CreateEntityResponse();
        response.Results["EntityId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables",
            OwnershipType = "UserOwned"
        };

        var act = () => serviceWithoutCache.CreateTableAsync(request);

        await act.Should().NotThrowAsync();
    }

    #endregion
}

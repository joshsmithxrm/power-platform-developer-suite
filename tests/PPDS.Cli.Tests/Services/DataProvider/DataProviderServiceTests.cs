using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.DataProvider;

public class DataProviderServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _mockPool;
    private readonly Mock<IPooledClient> _mockClient;
    private readonly Mock<ILogger<DataProviderService>> _mockLogger;
    private readonly DataProviderService _sut;

    private EntityCollection _retrieveMultipleResult = new();
    private Guid _createResult = Guid.Empty;
    private Entity? _updatedEntity;
    private readonly List<Entity> _deletedEntities = [];

    public DataProviderServiceTests()
    {
        _mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        _mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        _mockLogger = new Mock<ILogger<DataProviderService>>();

        // Async retrieve
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _retrieveMultipleResult);
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync(() => _retrieveMultipleResult);

        // Sync fallback
        _mockClient
            .Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(() => _retrieveMultipleResult);

        // Create
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _createResult);
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>()))
            .ReturnsAsync(() => _createResult);
        _mockClient
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(() => _createResult);

        // Update
        _mockClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => _updatedEntity = e)
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>()))
            .Callback<Entity>(e => _updatedEntity = e)
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.Update(It.IsAny<Entity>()))
            .Callback<Entity>(e => _updatedEntity = e);

        // Delete
        _mockClient
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((name, id, _) =>
            {
                var e = new Entity(name) { Id = id };
                _deletedEntities.Add(e);
            })
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>()))
            .Callback<string, Guid>((name, id) =>
            {
                var e = new Entity(name) { Id = id };
                _deletedEntities.Add(e);
            })
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.Delete(It.IsAny<string>(), It.IsAny<Guid>()))
            .Callback<string, Guid>((name, id) =>
            {
                var e = new Entity(name) { Id = id };
                _deletedEntities.Add(e);
            });

        // Pool
        _mockPool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockClient.Object);

        _sut = new DataProviderService(_mockPool.Object, _mockLogger.Object);
    }

    #region Helper builders

    private static Entity BuildDataSourceEntity(Guid id, string name, string? displayName = null, bool isManaged = false)
    {
        var e = new Entity("entitydatasource") { Id = id };
        e["name"] = name;
        e["displayname"] = displayName ?? name;
        e["description"] = (string?)null;
        e["ismanaged"] = isManaged;
        e["createdon"] = (DateTime?)null;
        e["modifiedon"] = (DateTime?)null;
        return e;
    }

    private static Entity BuildDataProviderEntity(
        Guid id,
        string name,
        string? dataSourceLogicalName = null,
        bool isManaged = false,
        Guid? retrievePlugin = null,
        Guid? retrieveMultiplePlugin = null,
        Guid? createPlugin = null,
        Guid? updatePlugin = null,
        Guid? deletePlugin = null)
    {
        var e = new Entity("entitydataprovider") { Id = id };
        e["name"] = name;
        e["datasourcelogicalname"] = dataSourceLogicalName;
        e["ismanaged"] = isManaged;
        e["retrieveplugin"] = retrievePlugin;
        e["retrievemultipleplugin"] = retrieveMultiplePlugin;
        e["createplugin"] = createPlugin;
        e["updateplugin"] = updatePlugin;
        e["deleteplugin"] = deletePlugin;
        e["createdon"] = (DateTime?)null;
        e["modifiedon"] = (DateTime?)null;
        return e;
    }

    #endregion

    #region ListDataSourcesAsync

    [Fact]
    public async Task ListDataSourcesAsync_ReturnsEmptyList_WhenNoneExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.ListDataSourcesAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListDataSourcesAsync_ReturnsDataSources_WhenTheyExist()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts", "Virtual Contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.ListDataSourcesAsync();

        Assert.Single(result);
        Assert.Equal(id, result[0].Id);
        Assert.Equal("cr123_contacts", result[0].Name);
        Assert.Equal("Virtual Contacts", result[0].DisplayName);
        Assert.False(result[0].IsManaged);
    }

    [Fact]
    public async Task ListDataSourcesAsync_MapsIsManaged_WhenTrue()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_managed", isManaged: true);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.ListDataSourcesAsync();

        Assert.Single(result);
        Assert.True(result[0].IsManaged);
    }

    #endregion

    #region GetDataSourceAsync

    [Fact]
    public async Task GetDataSourceAsync_ReturnsNull_WhenNotFound()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.GetDataSourceAsync("cr123_missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDataSourceAsync_FindsByName_WhenStringIsNotGuid()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts", "Virtual Contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetDataSourceAsync("cr123_contacts");

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
        Assert.Equal("cr123_contacts", result.Name);
    }

    [Fact]
    public async Task GetDataSourceAsync_FindsById_WhenStringIsValidGuid()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetDataSourceAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
    }

    #endregion

    #region RegisterDataSourceAsync

    [Fact]
    public async Task RegisterDataSourceAsync_ThrowsValidation_WhenNameIsEmpty()
    {
        var reg = new DataSourceRegistration(Name: "", DisplayName: "Virtual Contacts", Description: null);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterDataSourceAsync(reg));
        Assert.Equal(ErrorCodes.DataProvider.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterDataSourceAsync_ThrowsValidation_WhenDisplayNameIsEmpty()
    {
        var reg = new DataSourceRegistration(Name: "cr123_contacts", DisplayName: "", Description: null);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterDataSourceAsync(reg));
        Assert.Equal(ErrorCodes.DataProvider.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterDataSourceAsync_CreatesDataSource_WhenValid()
    {
        var expectedId = Guid.NewGuid();
        _createResult = expectedId;

        var reg = new DataSourceRegistration(
            Name: "cr123_contacts",
            DisplayName: "Virtual Contacts",
            Description: "Test data source");

        var result = await _sut.RegisterDataSourceAsync(reg);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == "entitydatasource"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterDataSourceAsync_SetsNameAndDisplayName_OnCreatedEntity()
    {
        _createResult = Guid.NewGuid();
        Entity? capturedEntity = null;
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(() => _createResult);

        var reg = new DataSourceRegistration(
            Name: "cr123_contacts",
            DisplayName: "Virtual Contacts",
            Description: null);

        await _sut.RegisterDataSourceAsync(reg);

        Assert.NotNull(capturedEntity);
        Assert.Equal("cr123_contacts", capturedEntity!["name"]);
        Assert.Equal("Virtual Contacts", capturedEntity["displayname"]);
    }

    #endregion

    #region UpdateDataSourceAsync

    [Fact]
    public async Task UpdateDataSourceAsync_ThrowsNotFound_WhenDataSourceDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var id = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateDataSourceAsync(id, new DataSourceUpdateRequest(DisplayName: "New Name")));
        Assert.Equal(ErrorCodes.DataSource.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateDataSourceAsync_UpdatesDisplayName_WhenProvided()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts", "Old Name");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateDataSourceAsync(id, new DataSourceUpdateRequest(DisplayName: "New Name"));

        Assert.NotNull(_updatedEntity);
        Assert.Equal("New Name", _updatedEntity!["displayname"]);
    }

    [Fact]
    public async Task UpdateDataSourceAsync_UpdatesDescription_WhenProvided()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateDataSourceAsync(id, new DataSourceUpdateRequest(Description: "New description"));

        Assert.NotNull(_updatedEntity);
        Assert.Equal("New description", _updatedEntity!["description"]);
    }

    [Fact]
    public async Task UpdateDataSourceAsync_DoesNotUpdate_WhenNoChanges()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateDataSourceAsync(id, new DataSourceUpdateRequest());

        _mockClient.Verify(
            s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region UnregisterDataSourceAsync

    [Fact]
    public async Task UnregisterDataSourceAsync_ThrowsNotFound_WhenDataSourceDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var id = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UnregisterDataSourceAsync(id));
        Assert.Equal(ErrorCodes.DataSource.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterDataSourceAsync_DeletesDataSource_WhenNoProviders()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataSourceEntity(id, "cr123_contacts");

        var callCount = 0;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new EntityCollection { Entities = { entity } }; // data source found
                return new EntityCollection(); // no dependent providers
            });

        await _sut.UnregisterDataSourceAsync(id);

        Assert.Single(_deletedEntities);
        Assert.Equal("entitydatasource", _deletedEntities[0].LogicalName);
        Assert.Equal(id, _deletedEntities[0].Id);
    }

    [Fact]
    public async Task UnregisterDataSourceAsync_ThrowsHasDependents_WhenProvidersExistWithoutProgress()
    {
        var id = Guid.NewGuid();
        var dataSource = BuildDataSourceEntity(id, "cr123_contacts");
        var provider = BuildDataProviderEntity(Guid.NewGuid(), "My Provider", "cr123_contacts");

        var callCount = 0;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new EntityCollection { Entities = { dataSource } };
                return new EntityCollection { Entities = { provider } };
            });

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UnregisterDataSourceAsync(id));
        Assert.Equal(ErrorCodes.DataSource.HasDependents, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterDataSourceAsync_CascadeDeletesProviders_WhenProgressReporterProvided()
    {
        var id = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var dataSource = BuildDataSourceEntity(id, "cr123_contacts");
        var provider = BuildDataProviderEntity(providerId, "My Provider", "cr123_contacts");

        var callCount = 0;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new EntityCollection { Entities = { dataSource } };
                return new EntityCollection { Entities = { provider } };
            });

        await _sut.UnregisterDataSourceAsync(id, force: true);

        // Should delete: 1 provider + 1 data source
        Assert.Equal(2, _deletedEntities.Count);
        Assert.Contains(_deletedEntities, e => e.LogicalName == "entitydataprovider" && e.Id == providerId);
        Assert.Contains(_deletedEntities, e => e.LogicalName == "entitydatasource" && e.Id == id);
    }

    #endregion

    #region ListDataProvidersAsync

    [Fact]
    public async Task ListDataProvidersAsync_ReturnsEmptyList_WhenNoneExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.ListDataProvidersAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListDataProvidersAsync_ReturnsProviders_WhenTheyExist()
    {
        var id = Guid.NewGuid();
        var retrievePluginId = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider", "cr123_contacts", retrievePlugin: retrievePluginId);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.ListDataProvidersAsync();

        Assert.Single(result);
        Assert.Equal(id, result[0].Id);
        Assert.Equal("My Provider", result[0].Name);
        Assert.Equal("cr123_contacts", result[0].DataSourceName);
        Assert.Equal(retrievePluginId, result[0].RetrievePlugin);
    }

    [Fact]
    public async Task ListDataProvidersAsync_FiltersBy_DataSourceId()
    {
        var dataSourceId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider", "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        QueryExpression? capturedQuery = null;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => capturedQuery = q as QueryExpression)
            .ReturnsAsync(() => _retrieveMultipleResult);

        await _sut.ListDataProvidersAsync(dataSourceId);

        // Verify a filter was applied
        Assert.NotNull(capturedQuery);
        Assert.True(capturedQuery!.Criteria.Conditions.Count > 0);
    }

    [Fact]
    public async Task ListDataProvidersAsync_MapsAllPluginBindings()
    {
        var id = Guid.NewGuid();
        var retrieve = Guid.NewGuid();
        var retrieveMultiple = Guid.NewGuid();
        var create = Guid.NewGuid();
        var update = Guid.NewGuid();
        var delete = Guid.NewGuid();
        var entity = BuildDataProviderEntity(
            id, "My Provider", "cr123_contacts",
            retrievePlugin: retrieve,
            retrieveMultiplePlugin: retrieveMultiple,
            createPlugin: create,
            updatePlugin: update,
            deletePlugin: delete);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.ListDataProvidersAsync();

        Assert.Single(result);
        Assert.Equal(retrieve, result[0].RetrievePlugin);
        Assert.Equal(retrieveMultiple, result[0].RetrieveMultiplePlugin);
        Assert.Equal(create, result[0].CreatePlugin);
        Assert.Equal(update, result[0].UpdatePlugin);
        Assert.Equal(delete, result[0].DeletePlugin);
    }

    #endregion

    #region GetDataProviderAsync

    [Fact]
    public async Task GetDataProviderAsync_ReturnsNull_WhenNotFound()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.GetDataProviderAsync("Missing Provider");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDataProviderAsync_FindsByName_WhenStringIsNotGuid()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider", "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetDataProviderAsync("My Provider");

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
        Assert.Equal("My Provider", result.Name);
    }

    [Fact]
    public async Task GetDataProviderAsync_FindsById_WhenStringIsValidGuid()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetDataProviderAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
    }

    #endregion

    #region RegisterDataProviderAsync

    [Fact]
    public async Task RegisterDataProviderAsync_ThrowsValidation_WhenNameIsEmpty()
    {
        var reg = new DataProviderRegistration(
            Name: "",
            DataSourceId: Guid.NewGuid(),
            RetrievePlugin: null,
            RetrieveMultiplePlugin: null,
            CreatePlugin: null,
            UpdatePlugin: null,
            DeletePlugin: null);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterDataProviderAsync(reg));
        Assert.Equal(ErrorCodes.DataProvider.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterDataProviderAsync_ThrowsNotFound_WhenDataSourceDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection(); // data source not found

        var reg = new DataProviderRegistration(
            Name: "My Provider",
            DataSourceId: Guid.NewGuid(),
            RetrievePlugin: null,
            RetrieveMultiplePlugin: null,
            CreatePlugin: null,
            UpdatePlugin: null,
            DeletePlugin: null);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterDataProviderAsync(reg));
        Assert.Equal(ErrorCodes.DataSource.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterDataProviderAsync_CreatesProvider_WhenValid()
    {
        var dataSourceId = Guid.NewGuid();
        var expectedId = Guid.NewGuid();
        _createResult = expectedId;

        var dataSource = BuildDataSourceEntity(dataSourceId, "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { dataSource } };

        var reg = new DataProviderRegistration(
            Name: "My Provider",
            DataSourceId: dataSourceId,
            RetrievePlugin: null,
            RetrieveMultiplePlugin: null,
            CreatePlugin: null,
            UpdatePlugin: null,
            DeletePlugin: null);

        var result = await _sut.RegisterDataProviderAsync(reg);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == "entitydataprovider"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterDataProviderAsync_SetsAllPluginBindings_WhenProvided()
    {
        var dataSourceId = Guid.NewGuid();
        _createResult = Guid.NewGuid();
        var dataSource = BuildDataSourceEntity(dataSourceId, "cr123_contacts");
        _retrieveMultipleResult = new EntityCollection { Entities = { dataSource } };

        Entity? capturedEntity = null;
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(() => _createResult);

        var retrieve = Guid.NewGuid();
        var retrieveMultiple = Guid.NewGuid();
        var create = Guid.NewGuid();
        var update = Guid.NewGuid();
        var delete = Guid.NewGuid();

        var reg = new DataProviderRegistration(
            Name: "My Provider",
            DataSourceId: dataSourceId,
            RetrievePlugin: retrieve,
            RetrieveMultiplePlugin: retrieveMultiple,
            CreatePlugin: create,
            UpdatePlugin: update,
            DeletePlugin: delete);

        await _sut.RegisterDataProviderAsync(reg);

        Assert.NotNull(capturedEntity);
        Assert.Equal(retrieve, capturedEntity!["retrieveplugin"]);
        Assert.Equal(retrieveMultiple, capturedEntity["retrievemultipleplugin"]);
        Assert.Equal(create, capturedEntity["createplugin"]);
        Assert.Equal(update, capturedEntity["updateplugin"]);
        Assert.Equal(delete, capturedEntity["deleteplugin"]);
    }

    #endregion

    #region UpdateDataProviderAsync

    [Fact]
    public async Task UpdateDataProviderAsync_ThrowsNotFound_WhenProviderDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var id = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateDataProviderAsync(id, new DataProviderUpdateRequest(RetrievePlugin: Guid.NewGuid())));
        Assert.Equal(ErrorCodes.DataProvider.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateDataProviderAsync_UpdatesRetrievePlugin_WhenProvided()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var newPlugin = Guid.NewGuid();
        await _sut.UpdateDataProviderAsync(id, new DataProviderUpdateRequest(RetrievePlugin: newPlugin));

        Assert.NotNull(_updatedEntity);
        Assert.Equal(newPlugin, _updatedEntity!["retrieveplugin"]);
    }

    [Fact]
    public async Task UpdateDataProviderAsync_UpdatesAllPlugins_WhenAllProvided()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var retrieve = Guid.NewGuid();
        var retrieveMultiple = Guid.NewGuid();
        var create = Guid.NewGuid();
        var update = Guid.NewGuid();
        var delete = Guid.NewGuid();

        await _sut.UpdateDataProviderAsync(id, new DataProviderUpdateRequest(
            RetrievePlugin: retrieve,
            RetrieveMultiplePlugin: retrieveMultiple,
            CreatePlugin: create,
            UpdatePlugin: update,
            DeletePlugin: delete));

        Assert.NotNull(_updatedEntity);
        Assert.Equal(retrieve, _updatedEntity!["retrieveplugin"]);
        Assert.Equal(retrieveMultiple, _updatedEntity["retrievemultipleplugin"]);
        Assert.Equal(create, _updatedEntity["createplugin"]);
        Assert.Equal(update, _updatedEntity["updateplugin"]);
        Assert.Equal(delete, _updatedEntity["deleteplugin"]);
    }

    [Fact]
    public async Task UpdateDataProviderAsync_DoesNotUpdate_WhenNoChanges()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateDataProviderAsync(id, new DataProviderUpdateRequest());

        _mockClient.Verify(
            s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region UnregisterDataProviderAsync

    [Fact]
    public async Task UnregisterDataProviderAsync_ThrowsNotFound_WhenProviderDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var id = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UnregisterDataProviderAsync(id));
        Assert.Equal(ErrorCodes.DataProvider.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterDataProviderAsync_DeletesProvider_WhenFound()
    {
        var id = Guid.NewGuid();
        var entity = BuildDataProviderEntity(id, "My Provider");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UnregisterDataProviderAsync(id);

        Assert.Single(_deletedEntities);
        Assert.Equal("entitydataprovider", _deletedEntities[0].LogicalName);
        Assert.Equal(id, _deletedEntities[0].Id);
    }

    #endregion
}

using System.Reflection;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

// Captures actual request construction; does not simulate Dataverse persistence.
public partial class PluginReconciliationWriteTests
{
    private readonly Mock<IPooledClient> _client = new();
    private readonly List<Entity> _writes = [];
    private readonly List<QueryExpression> _reads = [];
    private readonly Entity _existing = new("sdkmessageprocessingstep", Guid.NewGuid());
    private readonly PluginRegistrationService _service;

    public PluginReconciliationWriteTests()
    {
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => _reads.Add((QueryExpression)q))
            .ReturnsAsync(() => new EntityCollection(new List<Entity> { _existing }));
        _client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => _writes.Add(e)).Returns(Task.CompletedTask);
        _service = new PluginRegistrationService(pool.Object, new InactiveFakeShakedownGuard(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginRegistrationService>.Instance);
        PluginFieldCapabilityTestMetadata.AllowReadOnlyConnectionWrites(_client);
    }

    private static PluginStepConfig Step() => new()
    {
        Name = "research", Message = "Update", Entity = "account", Stage = "PostOperation", Mode = "Asynchronous"
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Step_ExplicitReset_WritesNullsAndFalse(string? reset)
    {
        var value = Step();
        value.FilteringAttributes = "name";
        value.UnsecureConfiguration = "public-config";
        value.Description = "description";
        value.RunAsUser = Guid.NewGuid().ToString();
        value.AsyncAutoDelete = true;
        value.CanBeBypassed = false;
        value.CanUseReadOnlyConnection = true;
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", value, Guid.NewGuid(), Guid.NewGuid(), identity: new StepIdentityResolution(_existing.Id));
        var first = Assert.Single(_writes);
        foreach (var key in new[] { "filteringattributes", "configuration", "description", "impersonatinguserid", "asyncautodelete", "canbebypassed", "canusereadonlyconnection", "sdkmessagefilterid" })
            Assert.True(first.Contains(key), key);

        var resetConfig = JsonSerializer.Deserialize<PluginStepConfig>("""{"name":"research","message":"Update","entity":"account","stage":"PostOperation","mode":"Asynchronous"}""")!;
        resetConfig.OperationIntent = RegistrationOperationIntent.Reconcile;
        resetConfig.FilteringAttributes = reset;
        resetConfig.UnsecureConfiguration = reset;
        resetConfig.Description = reset;
        resetConfig.RunAsUser = "CallingUser";
        resetConfig.AsyncAutoDelete = false;
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", resetConfig, Guid.NewGuid(), null, identity: new StepIdentityResolution(_existing.Id));
        Assert.Equal(2, _writes.Count);
        foreach (var key in new[] { "filteringattributes", "configuration", "description", "impersonatinguserid", "asyncautodelete" })
            Assert.True(_writes[1].Contains(key), key);
        foreach (var key in new[] { "filteringattributes", "configuration", "description", "impersonatinguserid" }) Assert.Null(_writes[1][key]);
        Assert.Equal(false, _writes[1]["asyncautodelete"]);
    }

    [Fact]
    public async Task ExplicitNullableBooleanDefaults_AreWritten()
    {
        var config = JsonSerializer.Deserialize<PluginStepConfig>("""{"name":"research","stage":"PostOperation","mode":"Asynchronous","canBeBypassed":true,"canUseReadOnlyConnection":false}""")!;
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        var write = Assert.Single(_writes);
        Assert.True(write.GetAttributeValue<bool>("canbebypassed"));
        Assert.True(write.Contains("canusereadonlyconnection"));
        Assert.False(write.GetAttributeValue<bool>("canusereadonlyconnection"));
        Assert.Contains("\"canBeBypassed\":true", JsonSerializer.Serialize(config));
        Assert.Contains("\"canUseReadOnlyConnection\":false", JsonSerializer.Serialize(config));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Image_ExplicitReset_WritesNulls(string? reset)
    {
        var config = JsonSerializer.Deserialize<PluginImageConfig>("""{"name":"pre","imageType":"PreImage","attributes":"name","description":"description"}""")!;
        await _service.UpsertImageAsync(Guid.NewGuid(), config, "Update");
        Assert.Equal("name", Assert.Single(_writes).GetAttributeValue<string>("attributes"));
        config.OperationIntent = RegistrationOperationIntent.Reconcile;
        config.Attributes = reset;
        config.Description = reset;
        await _service.UpsertImageAsync(Guid.NewGuid(), config, "Update");
        Assert.True(_writes[1].Contains("attributes"));
        Assert.True(_writes[1].Contains("description"));
        Assert.Null(_writes[1]["attributes"]); Assert.Null(_writes[1]["description"]);
        Assert.Equal("pre", _writes[1].GetAttributeValue<string>("entityalias"));
        Assert.Equal("Target", _writes[1].GetAttributeValue<string>("messagepropertyname"));
    }

    [Fact]
    public async Task SecureConfig_NullPreserves_EmptyStringWritesWithoutRetrievingValue()
    {
        var secureId = Guid.NewGuid();
        _existing["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", secureId);
        var config = Step();
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        Assert.Equal("sdkmessageprocessingstep", Assert.Single(_writes).LogicalName);
        Assert.False(_writes[0].Contains("sdkmessageprocessingstepsecureconfigid"));
        config.SecureConfiguration = "";
        await _service.UpsertStepAsync(Guid.NewGuid(), "pluginType", config, Guid.NewGuid(), null);
        Assert.Equal(3, _writes.Count);
        Assert.Equal("sdkmessageprocessingstepsecureconfig", _writes[1].LogicalName);
        Assert.Equal(secureId, _writes[1].Id);
        Assert.Equal("", _writes[1].GetAttributeValue<string>("secureconfig"));
        Assert.All(_reads, q => Assert.Equal("sdkmessageprocessingstep", q.EntityName));
        Assert.All(_reads, q => Assert.DoesNotContain("secureconfig", q.ColumnSet.Columns));
        _client.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}

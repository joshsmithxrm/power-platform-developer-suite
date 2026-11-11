using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public sealed class PluginStepFieldCapabilityTests
{
    private const string StepTable = "sdkmessageprocessingstep";
    private const string Attribute = "canusereadonlyconnection";

    [Fact]
    public async Task Reconcile_AlreadyFalseCreateOnlyField_IsOmittedWhileOtherChangesApply()
    {
        var f = new Fixture(actual: false);
        var config = Reconcile();
        config.Description = "changed description";

        var id = await f.Upsert(config);

        Assert.Equal(f.StepId, id);
        var update = Assert.Single(f.Updates);
        Assert.False(update.Contains(Attribute));
        Assert.Equal("changed description", update.GetAttributeValue<string>("description"));
        Assert.Contains(f.Reads, q => q.ColumnSet.Columns.Contains(Attribute));
        Assert.NotEmpty(f.MetadataReads);
    }

    [Fact]
    public async Task Reconcile_ImplicitResetOfTrueCreateOnlyField_RefusesBeforeSecureOrStepWrites()
    {
        var f = new Fixture(actual: true);
        var config = Reconcile();
        config.SecureConfiguration = "synthetic-noncredential-test-value";
        config.Enabled = false;

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(config));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_TrueCreateOnlyFieldAlreadyMatches_IsNotResent()
    {
        var f = new Fixture(actual: true);
        var config = Reconcile();
        config.CanUseReadOnlyConnection = true;

        await f.Upsert(config);

        Assert.False(Assert.Single(f.Updates).Contains(Attribute));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectUpdate_AlreadyEqualCreateOnlyField_IsOmitted(bool actual)
    {
        var f = new Fixture(actual);

        await f.Service.UpdateStepAsync(f.StepId,
            new StepUpdateRequest(Description: "changed", CanUseReadOnlyConnection: actual));

        var update = Assert.Single(f.Updates);
        Assert.False(update.Contains(Attribute));
        Assert.Equal("changed", update.GetAttributeValue<string>("description"));
    }

    [Fact]
    public async Task DirectUpdate_OnlyUnchangedCreateOnlyField_IsNoOp()
    {
        var f = new Fixture(actual: true);

        await f.Service.UpdateStepAsync(f.StepId,
            new StepUpdateRequest(CanUseReadOnlyConnection: true));

        Assert.Empty(f.Mutations);
        Assert.NotEmpty(f.MetadataReads);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DirectUpdate_ChangedCreateOnlyField_RefusesAllWrites(bool actual, bool desired)
    {
        var f = new Fixture(actual);

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Service.UpdateStepAsync(f.StepId,
            new StepUpdateRequest(Description: "must not be written", CanUseReadOnlyConnection: desired)));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task DirectUpdate_ExplicitResetOfTrueCreateOnlyField_RefusesAllWrites()
    {
        var f = new Fixture(actual: true);

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Service.UpdateStepAsync(f.StepId,
            new StepUpdateRequest(Description: "must not be written")
            { ResetFields = ["canUseReadOnlyConnection"] }));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task DirectUpdate_ExplicitResetAlreadyFalse_OmitsField()
    {
        var f = new Fixture(actual: false);

        await f.Service.UpdateStepAsync(f.StepId, new StepUpdateRequest(Description: "changed")
        { ResetFields = ["canUseReadOnlyConnection"] });

        Assert.False(Assert.Single(f.Updates).Contains(Attribute));
    }

    [Fact]
    public async Task Create_CreateCapabilityTrue_PreservesRequestedValue()
    {
        var f = new Fixture(actual: null, exists: false);
        var config = Reconcile();
        config.CanUseReadOnlyConnection = true;

        await f.Upsert(config);

        var created = Assert.Single(f.Creates);
        Assert.Equal(StepTable, created.LogicalName);
        Assert.True(created.GetAttributeValue<bool>(Attribute));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Create_UnsupportedOrUnknownCreateCapability_RefusesBeforeSecureWrites(bool? validCreate)
    {
        var f = new Fixture(actual: null, exists: false, validCreate: validCreate);
        var config = Reconcile();
        config.SecureConfiguration = "synthetic-noncredential-test-value";

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(config));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_UpdateCapabilityTrue_AllowsReset()
    {
        var f = new Fixture(actual: true, validUpdate: true);

        await f.Upsert(Reconcile());

        Assert.False(Assert.Single(f.Updates).GetAttributeValue<bool>(Attribute));
    }

    [Fact]
    public async Task DirectUpdate_UpdateCapabilityTrue_AllowsChange()
    {
        var f = new Fixture(actual: false, validUpdate: true);

        await f.Service.UpdateStepAsync(f.StepId, new StepUpdateRequest(CanUseReadOnlyConnection: true));

        Assert.True(Assert.Single(f.Updates).GetAttributeValue<bool>(Attribute));
    }

    [Fact]
    public async Task Reconcile_ActualFieldMissing_DoesNotAssumeFalse()
    {
        var f = new Fixture(actual: null);

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(Reconcile()));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Reconcile_ReadCapabilityNotConfirmed_DoesNotClaimUnchanged(bool? validRead)
    {
        var f = new Fixture(actual: false, validRead: validRead);

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(Reconcile()));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_UnknownUpdateCapabilityWithChangedValue_Refuses()
    {
        var f = new Fixture(actual: true, validUpdate: null);

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(Reconcile()));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_UnknownUpdateCapabilityEvenWhenEqual_DoesNotClaimSupportedNoOp()
    {
        var f = new Fixture(actual: false, validUpdate: null);

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(Reconcile()));

        Assert.Equal(ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_MetadataFailure_StopsBeforeSecureAndStepWrites()
    {
        var f = new Fixture(actual: false) { MetadataFailure = new InvalidOperationException("metadata unavailable") };
        var config = Reconcile();
        config.SecureConfiguration = "synthetic-noncredential-test-value";

        await Assert.ThrowsAnyAsync<Exception>(() => f.Upsert(config));

        Assert.NotEmpty(f.MetadataReads);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_MetadataForDifferentField_RefusesWithoutWrites()
    {
        var f = new Fixture(actual: false)
        {
            MetadataOverride = CapabilityFlags(new BooleanAttributeMetadata { LogicalName = "canbebypassed" })
        };

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(Reconcile()));

        Assert.Equal(ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Reconcile_MetadataWithWrongAttributeType_RefusesWithoutWrites()
    {
        var f = new Fixture(actual: false)
        {
            MetadataOverride = CapabilityFlags(new StringAttributeMetadata { LogicalName = Attribute })
        };

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Upsert(Reconcile()));

        Assert.Equal(ErrorCodes.Plugin.FieldCapabilityUnknown, error.ErrorCode);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Preflight_ChangedCreateOnlyField_RefusesWithoutMutations()
    {
        var f = new Fixture(actual: true);

        var error = await Assert.ThrowsAsync<PpdsException>(() =>
            f.Service.ValidateStepFieldCapabilitiesAsync(Reconcile(), f.StepId));

        AssertFieldDiagnostic(error);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task Preflight_NewStepCreateCapability_IsReadOnly()
    {
        var f = new Fixture(actual: null, exists: false);

        await f.Service.ValidateStepFieldCapabilitiesAsync(Reconcile(), null);

        Assert.NotEmpty(f.MetadataReads);
        Assert.Empty(f.Mutations);
    }

    [Fact]
    public async Task LegacyPatch_OmittedField_DoesNotRequireMetadataOrWriteDefault()
    {
        var f = new Fixture(actual: true) { MetadataFailure = new InvalidOperationException("must not request metadata") };
        var config = Reconcile();
        config.OperationIntent = RegistrationOperationIntent.Patch;
        config.Description = "other patch";

        await f.Upsert(config);

        Assert.Empty(f.MetadataReads);
        Assert.False(Assert.Single(f.Updates).Contains(Attribute));
    }

    private static PluginStepConfig Reconcile() => new()
    {
        Name = "Capability regression",
        Message = "Update",
        Entity = "contact",
        Stage = "PostOperation",
        Mode = "Asynchronous",
        OperationIntent = RegistrationOperationIntent.Reconcile
    };

    private static void AssertFieldDiagnostic(Exception error) =>
        Assert.Contains("canUseReadOnlyConnection", error.Message, StringComparison.OrdinalIgnoreCase);

    private static AttributeMetadata CapabilityFlags(AttributeMetadata metadata)
    {
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsValidForRead))!.SetValue(metadata, true);
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsValidForCreate))!.SetValue(metadata, true);
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsValidForUpdate))!.SetValue(metadata, false);
        return metadata;
    }

    private sealed class Fixture
    {
        private readonly Mock<IPooledClient> _client = new(MockBehavior.Loose);
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Loose);
        private readonly bool _exists;
        private readonly Entity _existing;
        private readonly BooleanAttributeMetadata _metadata;
        public Guid StepId { get; } = Guid.NewGuid();
        public Guid TypeId { get; } = Guid.NewGuid();
        public Guid MessageId { get; } = Guid.NewGuid();
        public List<QueryExpression> Reads { get; } = [];
        public List<RetrieveAttributeRequest> MetadataReads { get; } = [];
        public List<Entity> Creates { get; } = [];
        public List<Entity> Updates { get; } = [];
        public List<object> Mutations { get; } = [];
        public Exception? MetadataFailure { get; init; }
        public AttributeMetadata? MetadataOverride { get; init; }
        public PluginRegistrationService Service { get; }

        public Fixture(bool? actual, bool exists = true, bool? validRead = true,
            bool? validCreate = true, bool? validUpdate = false)
        {
            _exists = exists;
            _existing = new Entity(StepTable, StepId)
            {
                ["name"] = "Capability regression",
                ["statecode"] = new OptionSetValue(0),
                ["mode"] = new OptionSetValue(1),
                ["stage"] = new OptionSetValue(40),
                ["ismanaged"] = false,
                ["iscustomizable"] = new BooleanManagedProperty(true),
                ["message.name"] = new AliasedValue("sdkmessage", "name", "Update")
            };
            if (actual.HasValue) _existing[Attribute] = actual.Value;
            _metadata = new BooleanAttributeMetadata { LogicalName = Attribute };
            SetMetadataFlag(nameof(AttributeMetadata.IsValidForRead), validRead);
            SetMetadataFlag(nameof(AttributeMetadata.IsValidForCreate), validCreate);
            SetMetadataFlag(nameof(AttributeMetadata.IsValidForUpdate), validUpdate);

            _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns<QueryBase, CancellationToken>((query, _) => Task.FromResult(Read(query)));
            _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
                .Returns<QueryBase>(query => Task.FromResult(Read(query)));
            _client.Setup(c => c.RetrieveMultiple(It.IsAny<QueryBase>())).Returns<QueryBase>(Read);
            _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) => Task.FromResult(Execute(request)));
            _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>()))
                .Returns<OrganizationRequest>(request => Task.FromResult(Execute(request)));
            _client.Setup(c => c.Execute(It.IsAny<OrganizationRequest>())).Returns<OrganizationRequest>(Execute);
            _client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Returns<Entity, CancellationToken>((entity, _) => Task.FromResult(Create(entity)));
            _client.Setup(c => c.CreateAsync(It.IsAny<Entity>())).Returns<Entity>(entity => Task.FromResult(Create(entity)));
            _client.Setup(c => c.Create(It.IsAny<Entity>())).Returns<Entity>(Create);
            _client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((entity, _) => Update(entity)).Returns(Task.CompletedTask);
            _client.Setup(c => c.UpdateAsync(It.IsAny<Entity>())).Callback<Entity>(Update).Returns(Task.CompletedTask);
            _client.Setup(c => c.Update(It.IsAny<Entity>())).Callback<Entity>(Update);
            _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_client.Object);
            Service = new PluginRegistrationService(_pool.Object, new InactiveFakeShakedownGuard(),
                NullLogger<PluginRegistrationService>.Instance);
        }

        public Task<Guid> Upsert(PluginStepConfig config) => Service.UpsertStepAsync(
            TypeId, "pluginType", config, MessageId, null);

        private void SetMetadataFlag(string property, bool? value) =>
            typeof(AttributeMetadata).GetProperty(property)!.SetValue(_metadata, value);

        private EntityCollection Read(QueryBase query)
        {
            var typed = Assert.IsType<QueryExpression>(query);
            Reads.Add(typed);
            Assert.Equal(StepTable, typed.EntityName);
            if (!_exists) return new EntityCollection();
            var projected = new Entity(_existing.LogicalName, _existing.Id);
            foreach (var pair in _existing.Attributes)
                if (typed.ColumnSet.AllColumns || typed.ColumnSet.Columns.Contains(pair.Key) || pair.Key == "message.name")
                    projected[pair.Key] = pair.Value;
            return new EntityCollection([projected]);
        }

        private OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request is RetrieveAttributeRequest metadata)
            {
                MetadataReads.Add(metadata);
                Assert.Equal(StepTable, metadata.EntityLogicalName);
                Assert.Equal(Attribute, metadata.LogicalName);
                Assert.True(metadata.RetrieveAsIfPublished);
                if (MetadataFailure != null) throw MetadataFailure;
                return new RetrieveAttributeResponse
                { Results = new ParameterCollection { ["AttributeMetadata"] = MetadataOverride ?? _metadata } };
            }
            Mutations.Add(request);
            return new OrganizationResponse();
        }

        private Guid Create(Entity entity)
        {
            Creates.Add(entity);
            Mutations.Add(entity);
            return entity.LogicalName == StepTable ? StepId : Guid.NewGuid();
        }

        private void Update(Entity entity)
        {
            Updates.Add(entity);
            Mutations.Add(entity);
        }
    }
}

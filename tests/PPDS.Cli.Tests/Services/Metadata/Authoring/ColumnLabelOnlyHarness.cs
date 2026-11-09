using System.Runtime.Serialization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

// One strict fresh lease per operation. This harness never constructs an actual provider/client.
internal sealed class ColumnLabelHarness : IDisposable
{
    internal const string Table = "new_table";
    internal const string Column = "new_recordkey"; // Deliberately not an 'id' suffix.
    internal const string Schema = "new_RecordKey";
    internal static readonly Guid MetadataId = new("11111111-2222-3333-4444-555555555555");
    private readonly CancellationTokenSource _cancel = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
    private readonly Mock<ICachedMetadataProvider> _cache = new(MockBehavior.Strict);
    private readonly DataverseMetadataAuthoringService _service;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _lifecycle = [];
    private int _active, _disposals;
    private UpdateColumnRequest? _input;
    internal AttributeMetadata Attribute { get; }
    internal EntityMetadata? Entity { get; set; }
    internal List<OrganizationRequest> Requests { get; } = [];
    internal int CacheInvalidations { get; private set; }
    internal int Checkouts { get; private set; }
    internal int CheckoutAttempts { get; private set; }
    internal int FaultCheckout { get; set; }
    internal Exception? CheckoutFault { get; set; }
    internal string? AttributeResponseFault { get; set; }
    internal string? FaultOperation { get; set; }
    internal Exception? Fault { get; set; }
    internal Action<string>? AfterAcknowledgement { get; set; }
    internal CancellationToken Token => _cancel.Token;

    internal ColumnLabelHarness(string shape, ITestOutputHelper output, bool activeGuard = false)
    {
        _output = output;
        Attribute = Fresh(shape);
        Attribute.LogicalName = Column; Attribute.SchemaName = Schema; Attribute.MetadataId = MetadataId;
        Set(Attribute, "EntityLogicalName", Table);
        Set(Attribute, "IsPrimaryId", shape is "guid-primary" or "legacy-primary" ? true :
            shape is "guid-base-unknown-primary" or "guid-concrete-unknown-primary" ? (bool?)null : false);
        if (shape is "guid-base" or "legacy-primary" or "guid-base-unknown-primary")
        { Set(Attribute, "AttributeType", AttributeTypeCode.Uniqueidentifier); Set(Attribute, "AttributeTypeName", AttributeTypeDisplayName.UniqueidentifierType); }
        if (shape is "party-list" or "calendar-rules" or "virtual" or "base-null-type")
        {
            var type = shape switch { "party-list" => AttributeTypeCode.PartyList, "calendar-rules" => AttributeTypeCode.CalendarRules,
                "virtual" => AttributeTypeCode.Virtual, _ => (AttributeTypeCode?)null };
            Set(Attribute, "AttributeType", type);
        }
        Attribute.DisplayName = new Label("Original English", 1033);
        Attribute.DisplayName.LocalizedLabels.Add(new LocalizedLabel("Original French", 1036));
        Attribute.Description = new Label("Original description", 1033);
        Attribute.Description.LocalizedLabels.Add(new LocalizedLabel("Description francaise", 1036));
        Attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.SystemRequired);
        Attribute.IsCustomizable = new BooleanManagedProperty(true); Attribute.IsRenameable = new BooleanManagedProperty(true);
        Attribute.IsAuditEnabled = new BooleanManagedProperty(true); Attribute.IsSecured = true;
        Attribute.IsValidForAdvancedFind = new BooleanManagedProperty(true); Attribute.AutoNumberFormat = "fixture-unrelated";
        Set(Attribute, "IsManaged", true); Set(Attribute, "IsValidForUpdate", false);
        if (Attribute is EnumAttributeMetadata enumeration)
        {
            enumeration.DefaultFormValue = 1;
            enumeration.OptionSet = new OptionSetMetadata { Name = "fixture_options", IsGlobal = false };
            enumeration.OptionSet.Options.Add(new OptionMetadata(new Label("Unrelated option", 1033), 1));
        }
        Entity = new EntityMetadata { LogicalName = Table, MetadataId = new Guid("66666666-7777-8888-9999-000000000000") };
        Set(Entity, "PrimaryIdAttribute", Column.ToUpperInvariant());
        _pool.Setup(pool => pool.GetClientAsync(null, null, Token)).ReturnsAsync(Checkout);
        _cache.Setup(cache => cache.InvalidateEntity(Table)).Callback(() =>
        { Assert.Equal(0, _active); CacheInvalidations++; _lifecycle.Add("invalidate"); });
        IShakedownGuard guard = activeGuard ? new ActiveFakeShakedownGuard() : new InactiveFakeShakedownGuard();
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), guard, cacheProvider: _cache.Object);
    }

    internal Task UpdateAsync(UpdateColumnRequest request, IMetadataAuthoringProgressReporter? reporter = null)
    { _input = request; return _service.UpdateColumnAsync(request, reporter, Token); }
    internal void Cancel() => _cancel.Cancel();
    internal string SourceXml() => Serialize(Attribute).ToString(SaveOptions.DisableFormatting);

    private IPooledClient Checkout()
    {
        if (++CheckoutAttempts == FaultCheckout) { _lifecycle.Add("checkout-failed"); throw CheckoutFault!; }
        // Deliberately do not throw for a canceled token: the service must honor its own checkpoints.
        Assert.True(_slot.Wait(0), "A metadata operation retained the sole pool slot across a checkout.");
        Assert.Equal(0, _active); _active++; Checkouts++; _lifecycle.Add("checkout");
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        var released = false; var operations = 0;
        client.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            Assert.False(released); released = true; Assert.Equal(1, operations);
            _active--; _disposals++; _slot.Release(); _lifecycle.Add("dispose");
            return ValueTask.CompletedTask;
        });
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), Token)).Returns<OrganizationRequest, CancellationToken>((request, token) =>
        {
            Assert.False(released); Assert.Equal(1, ++operations); Assert.Equal(Token, token);
            Assert.NotNull(_input); Requests.Add(request); _lifecycle.Add(request.RequestName);
            OrganizationResponse response;
            switch (request)
            {
                case RetrieveAttributeRequest read:
                    Assert.Equal(_input.EntityLogicalName, read.EntityLogicalName, ignoreCase: true);
                    Assert.Equal(_input.ColumnLogicalName, read.LogicalName, ignoreCase: true);
                    Assert.False(read.RetrieveAsIfPublished);
                    response = AttributeResponseFault == "wrong-response" ? new OrganizationResponse()
                        : new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = AttributeResponseFault == "missing-metadata" ? null : Attribute } };
                    break;
                case RetrieveEntityRequest read:
                    Assert.Equal(_input.EntityLogicalName, read.LogicalName, ignoreCase: true);
                    Assert.Equal(EntityFilters.Entity, read.EntityFilters);
                    Assert.False(read.RetrieveAsIfPublished);
                    response = new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = Entity } };
                    break;
                case UpdateAttributeRequest update:
                    Assert.Equal(Table, update.EntityName, ignoreCase: true);
                    Assert.Equal("FixtureSolution", update.SolutionUniqueName);
                    response = new UpdateAttributeResponse(); break;
                case PublishXmlRequest publish:
                    Assert.Equal($"<importexportxml><entities><entity>{Table}</entity></entities></importexportxml>", publish.ParameterXml);
                    response = new PublishXmlResponse(); break;
                default: throw new InvalidOperationException("Unexpected synthetic SDK request.");
            }
            if (request.RequestName == FaultOperation) throw Fault!;
            AfterAcknowledgement?.Invoke(request.RequestName);
            return Task.FromResult(response);
        });
        return client.Object;
    }

    internal void AssertNoMutation(bool attributeReadOnly = false)
    {
        Assert.DoesNotContain(Requests, request => request is UpdateAttributeRequest or PublishXmlRequest);
        Assert.Equal(0, CacheInvalidations);
        if (attributeReadOnly) Assert.IsType<RetrieveAttributeRequest>(Assert.Single(Requests));
        AssertReleased();
    }

    internal void AssertAcknowledged(bool publish, bool tableRead)
    {
        var operations = new List<string> { "RetrieveAttribute" };
        if (tableRead) operations.Add("RetrieveEntity");
        operations.Add("UpdateAttribute");
        if (publish) operations.Add("PublishXml");
        Assert.Equal(operations, Requests.Select(request => request.RequestName));
        Assert.Equal(1, CacheInvalidations);
        var order = new List<string>();
        foreach (var operation in operations)
        {
            order.AddRange(["checkout", operation, "dispose"]);
            if (operation == "UpdateAttribute") order.Add("invalidate");
        }
        Assert.Equal(order, _lifecycle); AssertReleased();
    }

    private void AssertReleased()
    { Assert.Equal(0, _active); Assert.Equal(Checkouts, _disposals); Assert.Equal(1, _slot.CurrentCount); }

    internal static AttributeMetadata Fresh(string shape) => shape switch
    {
        "state" => new StateAttributeMetadata(), "status" => new StatusAttributeMetadata(),
        "guid-primary" or "guid-concrete" or "guid-concrete-unknown-primary" => new UniqueIdentifierAttributeMetadata(),
        "guid-base" or "legacy-primary" or "guid-base-unknown-primary" or "party-list" or "calendar-rules" or "virtual" or "base-null-type" => new AttributeMetadata(),
        "entity-name" => new EntityNameAttributeMetadata(), "managed-property" => new ManagedPropertyAttributeMetadata(),
        "string" => new StringAttributeMetadata(), "memo" => new MemoAttributeMetadata(), "integer" => new IntegerAttributeMetadata(),
        "bigint" => new BigIntAttributeMetadata(), "decimal" => new DecimalAttributeMetadata(), "double" => new DoubleAttributeMetadata(),
        "money" => new MoneyAttributeMetadata(), "boolean" => new BooleanAttributeMetadata(), "datetime" => new DateTimeAttributeMetadata(),
        "multiselect" => new MultiSelectPicklistAttributeMetadata(), "picklist" => new PicklistAttributeMetadata(),
        "image" => new ImageAttributeMetadata(), "file" => new FileAttributeMetadata(), "lookup" => new LookupAttributeMetadata(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    internal static void Set(object metadata, string property, object? value)
    {
        // Existing SDK fixtures use reflection for server-computed metadata with non-public setters.
        var member = metadata.GetType().GetProperty(property);
        Assert.NotNull(member); member.SetValue(metadata, value);
    }

    internal static XElement Serialize(AttributeMetadata metadata)
    {
        using var stream = new MemoryStream();
        new DataContractSerializer(typeof(AttributeMetadata)).WriteObject(stream, metadata);
        stream.Position = 0; return XElement.Load(stream);
    }

    public void Dispose()
    {
        AssertReleased();
        _output.WriteLine("PPDS_LABEL_BOUNDARY=" + JsonSerializer.Serialize(new { sdkRequests = Requests.Select(request => request.RequestName),
            checkoutAttempts = CheckoutAttempts, checkouts = Checkouts, disposals = _disposals, activeLeases = _active, cacheInvalidations = CacheInvalidations,
            onlySyntheticPool = true, realProviderOrAuth = false }));
        _slot.Dispose(); _cancel.Dispose();
    }
}

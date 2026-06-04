using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers AC-45 (InsertStatusValue called correctly), AC-46 (value derivation via OptionValueDeriver),
/// AC-47 (explicit collision throws), AC-48 (list projection), AC-49 (update/remove targeting + errors).
/// </summary>
[Trait("Category", "Unit")]
public class MetadataStatusReasonServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new(MockBehavior.Loose);
    private readonly DataverseMetadataAuthoringService _service;

    public MetadataStatusReasonServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        _service = new DataverseMetadataAuthoringService(
            _pool.Object,
            new SchemaValidator(),
            new InactiveFakeShakedownGuard());
    }

    private void SetupListStatusReasons(string entityLogicalName, IEnumerable<(int value, string label, int state)> options)
    {
        var statusOptions = options.Select(o =>
        {
            var opt = new StatusOptionMetadata();
            typeof(StatusOptionMetadata).GetProperty("Value")!.SetValue(opt, o.value);
            typeof(StatusOptionMetadata).GetProperty("State")!.SetValue(opt, o.state);
            opt.Label = new Label(o.label, 1033);
            return (OptionMetadata)opt;
        }).ToList();

        // OptionMetadataCollection is a DataCollection<OptionMetadata> — create and add items
        var collection = new OptionMetadataCollection();
        foreach (var item in statusOptions)
            collection.Add(item);

        var optionSet = new OptionSetMetadata();
        typeof(OptionSetMetadata)
            .GetProperty("Options", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(optionSet, collection);

        var attr = new StatusAttributeMetadata();
        (typeof(StatusAttributeMetadata).GetProperty("OptionSet")
            ?? typeof(EnumAttributeMetadata).GetProperty("OptionSet"))!
            .SetValue(attr, optionSet);

        var response = new RetrieveAttributeResponse();
        response.Results["AttributeMetadata"] = attr;

        _client.Setup(c => c.ExecuteAsync(
                It.Is<RetrieveAttributeRequest>(r =>
                    r.EntityLogicalName == entityLogicalName && r.LogicalName == "statuscode"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private void SetupInsertStatusValue(int assignedValue)
    {
        var response = new InsertStatusValueResponse();
        response.Results["NewOptionValue"] = assignedValue;

        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "InsertStatusValue"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private void SetupPublisherOptionValuePrefix(string solutionName, int prefix)
    {
        var publisherId = Guid.NewGuid();

        var solutionEntity = new Entity("solution")
        {
            ["publisherid"] = new EntityReference("publisher", publisherId)
        };
        var solutionCollection = new EntityCollection(new List<Entity> { solutionEntity });

        var publisherEntity = new Entity("publisher")
        {
            ["customizationoptionvalueprefix"] = prefix
        };
        var publisherCollection = new EntityCollection(new List<Entity> { publisherEntity });

        _client.SetupSequence(c => c.RetrieveMultipleAsync(
                It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(solutionCollection)
            .ReturnsAsync(publisherCollection);
    }

    // AC-45: InsertStatusValue is called with correct entity/attribute/stateCode

    [Fact]
    public async Task AddStatusReasonAsync_WithExplicitValue_CallsInsertStatusValue()
    {
        SetupListStatusReasons("account", [(1, "Active", 0)]);
        SetupInsertStatusValue(99);

        var request = new AddStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "NewStatus",
            StateCode = 0,
            Value = 99
        };

        var result = await _service.AddStatusReasonAsync(request);

        result.Should().Be(99);
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "InsertStatusValue"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // AC-46: Value is derived via OptionValueDeriver when --solution provided

    [Fact]
    public async Task AddStatusReasonAsync_WithSolutionPrefix_DerivesValue()
    {
        SetupListStatusReasons("account", [(1, "Active", 0)]);
        SetupPublisherOptionValuePrefix("MySolution", 10);
        SetupInsertStatusValue(100_000);

        var request = new AddStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "NewStatus",
            StateCode = 0,
            SolutionUniqueName = "MySolution"
        };

        var result = await _service.AddStatusReasonAsync(request);

        result.Should().Be(100_000);
    }

    // AC-47: Explicit value collision throws

    [Fact]
    public async Task AddStatusReasonAsync_ExplicitValueCollision_Throws()
    {
        SetupListStatusReasons("account", [(99, "Active", 0)]);

        var request = new AddStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "Conflict",
            StateCode = 0,
            Value = 99
        };

        var act = async () => await _service.AddStatusReasonAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.DuplicateOptionValue);
    }

    // AC-48: List projection returns correct StatusReasonInfo

    [Fact]
    public async Task ListStatusReasonsAsync_ProjectsStatusOptionMetadataCorrectly()
    {
        SetupListStatusReasons("account", [
            (1, "Active", 0),
            (2, "Inactive", 1)
        ]);

        var result = await _service.ListStatusReasonsAsync("account");

        result.Should().HaveCount(2);

        var active = result.First(r => r.Value == 1);
        active.Label.Should().Be("Active");
        active.StateCode.Should().Be(0);
        active.StateLabel.Should().Be("Active");

        var inactive = result.First(r => r.Value == 2);
        inactive.Label.Should().Be("Inactive");
        inactive.StateCode.Should().Be(1);
        inactive.StateLabel.Should().Be("Inactive");
    }

    // AC-49: UpdateStatusReason targeting by label — resolves to value, then calls UpdateOptionValue

    [Fact]
    public async Task UpdateStatusReasonAsync_ByLabel_ResolvesValueAndUpdates()
    {
        SetupListStatusReasons("account", [(1, "Active", 0), (2, "Inactive", 1)]);

        var request = new UpdateStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "Active",
            NewLabel = "ActiveUpdated"
        };

        await _service.UpdateStatusReasonAsync(request);

        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "UpdateOptionValue"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateStatusReasonAsync_ByLabelNotFound_ThrowsOptionNotFound()
    {
        SetupListStatusReasons("account", [(1, "Active", 0)]);

        var request = new UpdateStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "NonExistent",
            NewLabel = "Updated"
        };

        var act = async () => await _service.UpdateStatusReasonAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    // AC-49: RemoveStatusReason targeting by label

    [Fact]
    public async Task RemoveStatusReasonAsync_ByLabel_ResolvesValueAndDeletes()
    {
        SetupListStatusReasons("account", [(1, "Active", 0), (2, "Inactive", 1)]);

        var request = new RemoveStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "Active"
        };

        await _service.RemoveStatusReasonAsync(request);

        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "DeleteOptionValue"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveStatusReasonAsync_ByLabelNotFound_ThrowsOptionNotFound()
    {
        SetupListStatusReasons("account", [(1, "Active", 0)]);

        var request = new RemoveStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "NonExistent"
        };

        var act = async () => await _service.RemoveStatusReasonAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    [Fact]
    public async Task UpdateStatusReasonAsync_ByValueNotFound_ThrowsOptionNotFound()
    {
        SetupListStatusReasons("account", [(1, "Active", 0)]);

        var request = new UpdateStatusReasonRequest
        {
            EntityLogicalName = "account",
            Value = 999,
            NewLabel = "Updated"
        };

        var act = async () => await _service.UpdateStatusReasonAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    [Fact]
    public async Task RemoveStatusReasonAsync_ByValueNotFound_ThrowsOptionNotFound()
    {
        SetupListStatusReasons("account", [(1, "Active", 0)]);

        var request = new RemoveStatusReasonRequest
        {
            EntityLogicalName = "account",
            Value = 999
        };

        var act = async () => await _service.RemoveStatusReasonAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    [Fact]
    public async Task AddStatusReasonAsync_DryRun_DoesNotCallSdk()
    {
        SetupListStatusReasons("account", []);

        var request = new AddStatusReasonRequest
        {
            EntityLogicalName = "account",
            Label = "Test",
            StateCode = 0,
            Value = 100,
            DryRun = true
        };

        var result = await _service.AddStatusReasonAsync(request);

        result.Should().Be(100);
        _client.Verify(c => c.ExecuteAsync(
            It.Is<OrganizationRequest>(r => r.RequestName == "InsertStatusValue"),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}

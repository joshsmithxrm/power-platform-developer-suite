using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOptionValueRequest = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers #1161 local (column-scoped) option management on the authoring service (AC-55, AC-56),
/// and the option-color mechanism. Color is NOT carried by the Insert/Update OptionValue messages
/// (those have no Color parameter — request["Color"] is silently dropped by the platform); it must be
/// applied via OptionMetadata.Color on the retrieved attribute, re-sent through UpdateAttribute. These
/// tests assert that the service issues exactly that follow-up request.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataLocalOptionServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly DataverseMetadataAuthoringService _service;

    // Stateful backing store so a post-insert retrieve (used by the color step) sees the new option.
    private readonly List<OptionMetadata> _options = new();
    private OrganizationRequest? _captured;
    private int _optionWrites;
    private int _attributeReads;

    public MetadataLocalOptionServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
    }

    /// <summary>
    /// Seeds the column's local option set and wires the mock client to serve RetrieveAttribute from the
    /// live <see cref="_options"/> list and capture Insert/Update/Delete OptionValue. A Color parameter on an insert or
    /// update is stored on the option, as Dataverse does (observed live), so the colour read-back sees it.
    /// </summary>
    private void SetupColumnOptions(params (int value, string label)[] options)
    {
        foreach (var (value, label) in options)
            _options.Add(new OptionMetadata(new Label(label, 1033), value));

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                switch (req)
                {
                    case RetrieveAttributeRequest:
                    {
                        _attributeReads++;
                        var optionSet = new OptionSetMetadata { IsGlobal = false };
                        foreach (var o in _options)
                            optionSet.Options.Add(o);
                        var picklist = new PicklistAttributeMetadata { OptionSet = optionSet };
                        var response = new RetrieveAttributeResponse();
                        response.Results["AttributeMetadata"] = picklist;
                        return Task.FromResult<OrganizationResponse>(response);
                    }
                    case InsertOptionValueRequest ins:
                        _captured = req;
                        var insertedValue = ins.Value ?? 0;
                        _optionWrites++;
                        _options.Add(new OptionMetadata(ins.Label, insertedValue) { Color = ins.Parameters.TryGetValue("Color", out var c) ? (string)c : null });
                        var insResponse = new InsertOptionValueResponse();
                        insResponse.Results["NewOptionValue"] = insertedValue;
                        return Task.FromResult<OrganizationResponse>(insResponse);
                    case SdkUpdateOptionValueRequest update:
                        _captured = req;
                        _optionWrites++;
                        if (update.Parameters.TryGetValue("Color", out var color))
                            _options.Single(o => o.Value == update.Value).Color = (string)color;
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                    case SdkDeleteOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new DeleteOptionValueResponse());
                    default:
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

    private string? StoredColorFor(int value) => _options.Single(o => o.Value == value).Color;

    [Fact]
    public async Task AddColumnOption_ExplicitValue_InsertsScopedToColumn() // AC-55
    {
        SetupColumnOptions((864630000, "Mild"));

        var assigned = await _service.AddColumnOptionAsync(new AddColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Moderate",
            Value = 864630001
        });

        assigned.Should().Be(864630001);
        var insert = _captured.Should().BeOfType<InsertOptionValueRequest>().Subject;
        insert.Value.Should().Be(864630001);
        insert["EntityLogicalName"].Should().Be("hsl_diagnosis");
        insert["AttributeLogicalName"].Should().Be("hsl_severity");
        // No color requested → no Color parameter and no colour read-back (only the allocation read).
        insert.Parameters.ContainsKey("Color").Should().BeFalse();
        _optionWrites.Should().Be(1);
    }

    [Fact]
    public async Task AddColumnOption_WithColor_CarriesColorOnTheInsert() // AC-55 + AC-146
    {
        SetupColumnOptions((864630000, "Mild"));

        await _service.AddColumnOptionAsync(new AddColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Moderate",
            Value = 864630001,
            Color = "#FF8800"
        });

        // One write: the insert carries the Color parameter; the option is then read back to confirm it.
        ((InsertOptionValueRequest)_captured!)["Color"].Should().Be("#FF8800");
        _optionWrites.Should().Be(1);
        StoredColorFor(864630001).Should().Be("#FF8800");
    }

    [Fact]
    public async Task AddColumnOption_NeitherValueNorSolution_Throws() // AC-55
    {
        var act = () => _service.AddColumnOptionAsync(new AddColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Moderate"
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.MissingRequiredField);
    }

    [Fact]
    public async Task RemoveColumnOption_ByLabel_ResolvesAndDeletes() // AC-56
    {
        SetupColumnOptions((864630000, "Mild"), (864630001, "Severe"));

        await _service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Severe"
        });

        var del = _captured.Should().BeOfType<SdkDeleteOptionValueRequest>().Subject;
        del.Value.Should().Be(864630001);
        del["AttributeLogicalName"].Should().Be("hsl_severity");
    }

    [Fact]
    public async Task RemoveColumnOption_ValueNotFound_ThrowsOptionNotFound() // AC-56
    {
        SetupColumnOptions((864630000, "Mild"));

        var act = () => _service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Value = 999999
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    [Fact]
    public async Task UpdateColumnOption_ByValue_UpdatesScoped() // AC-56
    {
        SetupColumnOptions((864630000, "Mild"));

        await _service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Value = 864630000,
            NewLabel = "Minimal"
        });

        var upd = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        upd.Value.Should().Be(864630000);
        upd["AttributeLogicalName"].Should().Be("hsl_severity");
        // No color requested → no Color parameter.
        upd.Parameters.ContainsKey("Color").Should().BeFalse();
        _optionWrites.Should().Be(1);
    }

    [Fact]
    public async Task UpdateColumnOption_WithLabelAndColor_SendsOneRequestCarryingBoth() // AC-56 + AC-146
    {
        SetupColumnOptions((864630000, "Mild"));

        await _service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Value = 864630000,
            NewLabel = "Minimal",
            Color = "#00FF00"
        });

        // S8: one UpdateOptionValue carries the new label and the Color parameter (observed live to store both).
        _optionWrites.Should().Be(1);
        var update = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        update["EntityLogicalName"].Should().Be("hsl_diagnosis");
        update.Label.LocalizedLabels.Should().ContainSingle().Which.Label.Should().Be("Minimal");
        update["Color"].Should().Be("#00FF00");
        StoredColorFor(864630000).Should().Be("#00FF00");
    }

    // ---- ambiguous --label resolution on duplicate labels (#1235) ----

    [Fact]
    public async Task RemoveColumnOption_DuplicateLabel_ThrowsAmbiguousListingAllValues() // #1235
    {
        // Two column options share the label "Severe" (legal in Dataverse). Removing by label must
        // refuse to act rather than silently delete the first match.
        SetupColumnOptions((864630000, "Severe"), (864630001, "Severe"));

        var act = () => _service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Severe"
        });

        var assertion = await act.Should().ThrowAsync<PpdsException>();
        assertion.Which.ErrorCode.Should().Be(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel);
        assertion.Which.Message.Should().Contain("864630000").And.Contain("864630001");
        // Nothing was mutated — resolution threw before any SDK delete.
        _captured.Should().BeNull();
    }

    [Fact]
    public async Task UpdateColumnOption_DuplicateLabel_ThrowsAmbiguousListingAllValues() // #1235
    {
        SetupColumnOptions((864630000, "Severe"), (864630001, "Severe"));

        var act = () => _service.UpdateColumnOptionAsync(new UpdateColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Severe",
            NewLabel = "Critical"
        });

        var assertion = await act.Should().ThrowAsync<PpdsException>();
        assertion.Which.ErrorCode.Should().Be(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel);
        assertion.Which.Message.Should().Contain("864630000").And.Contain("864630001");
        _captured.Should().BeNull();
    }

    [Fact]
    public async Task RemoveColumnOption_UniqueLabelAmongDuplicates_ResolvesSingleMatch() // #1235
    {
        // A label that matches exactly one option still resolves, even when others share a different label.
        SetupColumnOptions((864630000, "Severe"), (864630001, "Severe"), (864630002, "Mild"));

        await _service.RemoveColumnOptionAsync(new RemoveColumnOptionRequest
        {
            EntityLogicalName = "hsl_diagnosis",
            ColumnLogicalName = "hsl_severity",
            Label = "Mild"
        });

        _captured.Should().BeOfType<SdkDeleteOptionValueRequest>()
            .Which.Value.Should().Be(864630002);
    }
}

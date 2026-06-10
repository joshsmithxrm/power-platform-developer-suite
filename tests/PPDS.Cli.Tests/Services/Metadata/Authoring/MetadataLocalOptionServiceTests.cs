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
    private UpdateAttributeRequest? _capturedColorUpdate;

    public MetadataLocalOptionServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
    }

    /// <summary>
    /// Seeds the column's local option set and wires the mock client to serve RetrieveAttribute from the
    /// live <see cref="_options"/> list, capture Insert/Update/Delete OptionValue, and capture the
    /// UpdateAttribute color follow-up.
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
                        _options.Add(new OptionMetadata(ins.Label, insertedValue));
                        var insResponse = new InsertOptionValueResponse();
                        insResponse.Results["NewOptionValue"] = insertedValue;
                        return Task.FromResult<OrganizationResponse>(insResponse);
                    case SdkUpdateOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                    case SdkDeleteOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new DeleteOptionValueResponse());
                    case UpdateAttributeRequest upd:
                        _capturedColorUpdate = upd;
                        return Task.FromResult<OrganizationResponse>(new UpdateAttributeResponse());
                    default:
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

    /// <summary>Reads the color the service applied to <paramref name="value"/> via UpdateAttribute.</summary>
    private string? CapturedColorFor(int value)
        => ((_capturedColorUpdate?.Attribute as EnumAttributeMetadata)?.OptionSet?.Options)
            ?.FirstOrDefault(o => o.Value == value)?.Color;

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
        // No color requested → no UpdateAttribute follow-up.
        _capturedColorUpdate.Should().BeNull();
    }

    [Fact]
    public async Task AddColumnOption_WithColor_AppliesColorViaUpdateAttribute() // AC-55 + color (Gemini #review)
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

        // The Insert message does not carry color — a follow-up UpdateAttribute sets OptionMetadata.Color.
        ((InsertOptionValueRequest)_captured!).Parameters.ContainsKey("Color").Should().BeFalse();
        _capturedColorUpdate.Should().NotBeNull();
        _capturedColorUpdate!.EntityName.Should().Be("hsl_diagnosis");
        CapturedColorFor(864630001).Should().Be("#FF8800");
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
        // No color requested → no UpdateAttribute follow-up.
        _capturedColorUpdate.Should().BeNull();
    }

    [Fact]
    public async Task UpdateColumnOption_WithColor_AppliesColorViaUpdateAttribute() // AC-56 + color (Gemini #review)
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

        // Label change still goes through UpdateOptionValue (without a Color parameter)...
        var update = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        update.Parameters.ContainsKey("Color").Should().BeFalse();
        // ...but color is applied via the documented OptionMetadata.Color + UpdateAttribute mechanism.
        _capturedColorUpdate.Should().NotBeNull();
        _capturedColorUpdate!.EntityName.Should().Be("hsl_diagnosis");
        CapturedColorFor(864630000).Should().Be("#00FF00");
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

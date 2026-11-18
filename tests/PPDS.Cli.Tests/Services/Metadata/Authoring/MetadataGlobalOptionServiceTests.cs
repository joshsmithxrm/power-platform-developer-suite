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
using SdkDeleteOptionValueRequest = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers global (option-set-scoped) option management on the authoring service: value/label targeting +
/// dry-run (#1169/#1170/#1172) and option color. Observed live: an option's colour is stored only through the Color
/// parameter of InsertOptionValue / UpdateOptionValue; OptionMetadata.Color re-sent through UpdateOptionSet is accepted
/// and ignored. The service puts Color on the one insert/update request and then reads the option back to confirm it.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataGlobalOptionServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly DataverseMetadataAuthoringService _service;

    // Stateful backing store so the post-insert retrieve (color follow-up) sees the new option.
    private readonly List<OptionMetadata> _options = new();
    private OrganizationRequest? _captured;
    private RetrieveOptionSetRequest? _retrieveRequest;
    private bool _ignoreColor;

    public MetadataGlobalOptionServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard(), baseLanguage: new FixedOrganizationBaseLanguage(1033));

        // Default mock: stateful add-option (Insert, which stores a Color parameter) plus the colour read-back.
        // Tests that target an existing option by value/label call SetupGlobalOptions, which re-setups the
        // client with a fixed option set for Retrieve/Delete/Update.
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                switch (req)
                {
                    case InsertOptionValueRequest insert:
                        _captured = req;
                        var insertedValue = insert.Value ?? 0;
                        _options.Add(new OptionMetadata(insert.Label, insertedValue) { Color = _ignoreColor ? null : insert.Parameters.TryGetValue("Color", out var c) ? (string)c : null });
                        var ins = new InsertOptionValueResponse();
                        ins.Results["NewOptionValue"] = insertedValue;
                        return Task.FromResult<OrganizationResponse>(ins);
                    case SdkUpdateOptionValueRequest update:
                        _captured = req;
                        StoreColor(_options, update);
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                    case RetrieveOptionSetRequest retrieve:
                    {
                        _retrieveRequest = retrieve;
                        var optionSet = new OptionSetMetadata { IsGlobal = true };
                        foreach (var o in _options)
                            optionSet.Options.Add(o);
                        var resp = new RetrieveOptionSetResponse();
                        resp.Results["OptionSetMetadata"] = optionSet;
                        return Task.FromResult<OrganizationResponse>(resp);
                    }
                    default:
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

    /// <summary>Sets up the global option set returned by RetrieveOptionSet, and captures mutations.</summary>
    private void SetupGlobalOptions(params (int value, string label)[] options)
    {
        var optionSet = new OptionSetMetadata { Name = "new_mystatus", IsGlobal = true };
        foreach (var (value, label) in options)
            optionSet.Options.Add(new OptionMetadata(new Label(label, 1033), value));
        var retrieveResponse = new RetrieveOptionSetResponse();
        retrieveResponse.Results["OptionSetMetadata"] = optionSet;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                switch (req)
                {
                    case RetrieveOptionSetRequest retrieve:
                        _retrieveRequest = retrieve;
                        return Task.FromResult<OrganizationResponse>(retrieveResponse);
                    case SdkUpdateOptionValueRequest update:
                        _captured = req;
                        StoreColor(optionSet.Options, update);
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                    case SdkDeleteOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new DeleteOptionValueResponse());
                    default:
                        _captured = req;
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

    /// <summary>Models Dataverse storing the Color parameter on the addressed option (unless told to ignore it).</summary>
    private void StoreColor(IEnumerable<OptionMetadata> options, SdkUpdateOptionValueRequest update)
    {
        var option = options.Single(o => o.Value == update.Value);
        if (!_ignoreColor && update.Parameters.TryGetValue("Color", out var color))
            option.Color = (string)color;
        // Labels and descriptions are stored too (merged by LCID), so the read-back sees what was written.
        if (update.Parameters.TryGetValue("Label", out var label) && label is Label written) option.Label = Merge(option.Label, written);
        if (update.Parameters.TryGetValue("Description", out var description) && description is Label text) option.Description = Merge(option.Description, text);
    }

    private static Label Merge(Label? stored, Label written)
    {
        var merged = new Label();
        foreach (var l in stored?.LocalizedLabels ?? []) if (written.LocalizedLabels.All(w => w.LanguageCode != l.LanguageCode)) merged.LocalizedLabels.Add(l);
        foreach (var l in written.LocalizedLabels) merged.LocalizedLabels.Add(l);
        return merged;
    }

    // ---- add/update-option color forwarding (#1233) ----

    [Fact]
    public async Task AddOptionValue_WithColor_CarriesColorOnTheInsert_AndReadsItBack()
    {
        var assigned = await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "hsl_severity",
            SolutionUniqueName = "MySolution",
            Label = "Severe",
            Value = 864630001,
            Color = "#FF0000"
        });

        assigned.Should().Be(864630001);

        // The colour rides on the insert (one write); the option is then read back as-if-published.
        var insert = _captured.Should().BeOfType<InsertOptionValueRequest>().Subject;
        insert.Value.Should().Be(864630001);
        insert["Color"].Should().Be("#FF0000");
        _retrieveRequest.Should().NotBeNull();
        _retrieveRequest!.RetrieveAsIfPublished.Should().BeTrue();
        _client.Verify(c => c.ExecuteAsync(It.IsAny<SdkUpdateOptionValueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddOptionValue_WithoutColor_DoesNotSetColorParameter()
    {
        await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "hsl_severity",
            SolutionUniqueName = "MySolution",
            Label = "Mild",
            Value = 864630000
        });

        var insert = _captured.Should().BeOfType<InsertOptionValueRequest>().Subject;
        insert.Parameters.ContainsKey("Color").Should().BeFalse();
        // The label is still read back (one as-if-published read), with no colour to confirm.
        _retrieveRequest.Should().NotBeNull();
        _retrieveRequest!.RetrieveAsIfPublished.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateOptionValue_WithLabelAndColor_SendsOneRequestCarryingBoth()
    {
        _options.Add(new OptionMetadata(new Label("Existing", 1033), 864630000));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            OptionSetName = "hsl_severity",
            SolutionUniqueName = "MySolution",
            Value = 864630000,
            NewLabel = "Renamed",
            Color = "#112233"
        });

        // One UpdateOptionValue carries the label and the colour, so neither can commit without the other.
        _client.Verify(c => c.ExecuteAsync(It.IsAny<SdkUpdateOptionValueRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var update = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        update.Label.LocalizedLabels.Should().ContainSingle().Which.Label.Should().Be("Renamed");
        update["Color"].Should().Be("#112233");
        update.SolutionUniqueName.Should().Be("MySolution");
    }

    // ---- remove-option targeting by value/label (#1169) ----

    [Fact]
    public async Task DeleteOptionValue_ByLabel_ResolvesAndDeletes() // #1169
    {
        SetupGlobalOptions((100000000, "Draft"), (100000001, "Approved"));

        await _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Approved"
        });

        var del = _captured.Should().BeOfType<SdkDeleteOptionValueRequest>().Subject;
        del.Value.Should().Be(100000001);
        del.OptionSetName.Should().Be("new_mystatus");
    }

    [Fact]
    public async Task DeleteOptionValue_ByLabel_IsCaseInsensitive() // #1169
    {
        SetupGlobalOptions((100000000, "Draft"));

        await _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "dRaFt"
        });

        _captured.Should().BeOfType<SdkDeleteOptionValueRequest>()
            .Which.Value.Should().Be(100000000);
    }

    [Fact]
    public async Task DeleteOptionValue_LabelNotFound_ThrowsOptionNotFound() // #1169
    {
        SetupGlobalOptions((100000000, "Draft"));

        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Nope"
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    [Fact]
    public async Task DeleteOptionValue_ValueNotFound_ThrowsOptionNotFound() // #1169
    {
        SetupGlobalOptions((100000000, "Draft"));

        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 999999
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }

    [Fact]
    public async Task DeleteOptionValue_NeitherValueNorLabel_ThrowsMissingRequiredField() // #1169
    {
        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus"
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.MissingRequiredField);
    }

    [Fact]
    public async Task DeleteOptionValue_Resolution_RetrievesAsIfPublished() // #1169
    {
        // A just-added option is unpublished; resolution must see unpublished metadata
        // or value/label targeting regresses for the add-then-remove flow.
        SetupGlobalOptions((100000000, "Draft"));

        await _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 100000000
        });

        _retrieveRequest.Should().NotBeNull();
        _retrieveRequest!.RetrieveAsIfPublished.Should().BeTrue();
    }

    // ---- ambiguous --label resolution on duplicate labels (#1235) ----

    [Fact]
    public async Task DeleteOptionValue_DuplicateLabel_ThrowsAmbiguousListingAllValues() // #1235
    {
        // Two options share the label "Draft" (legal in Dataverse). Removing by label must refuse
        // to act rather than silently delete the first match.
        SetupGlobalOptions((100000000, "Draft"), (100000001, "Draft"));

        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Draft"
        });

        var assertion = await act.Should().ThrowAsync<PpdsException>();
        assertion.Which.ErrorCode.Should().Be(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel);
        assertion.Which.Message.Should().Contain("100000000").And.Contain("100000001");
        // Nothing was mutated — resolution threw before any SDK delete.
        _captured.Should().BeNull();
    }

    [Fact]
    public async Task UpdateOptionValue_DuplicateLabel_ThrowsAmbiguousListingAllValues() // #1235
    {
        SetupGlobalOptions((100000000, "Draft"), (100000001, "Draft"));

        var act = () => _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Draft",
            NewLabel = "Accepted"
        });

        var assertion = await act.Should().ThrowAsync<PpdsException>();
        assertion.Which.ErrorCode.Should().Be(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel);
        assertion.Which.Message.Should().Contain("100000000").And.Contain("100000001");
        _captured.Should().BeNull();
    }

    [Fact]
    public async Task DeleteOptionValue_CaseInsensitiveDuplicateLabel_ThrowsAmbiguous() // #1235
    {
        // Resolution is case-insensitive, so labels that differ only in case are duplicates too and
        // a label selector that case-insensitively matches both must be rejected as ambiguous.
        SetupGlobalOptions((100000000, "Draft"), (100000001, "draft"));

        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "DRAFT"
        });

        var assertion = await act.Should().ThrowAsync<PpdsException>();
        assertion.Which.ErrorCode.Should().Be(ErrorCodes.MetadataAuthoring.AmbiguousOptionLabel);
        assertion.Which.Message.Should().Contain("100000000").And.Contain("100000001");
        _captured.Should().BeNull();
    }

    [Fact]
    public async Task DeleteOptionValue_UniqueLabelAmongDuplicates_ResolvesSingleMatch() // #1235
    {
        // A label that matches exactly one option still resolves, even when other options share a different label.
        SetupGlobalOptions((100000000, "Draft"), (100000001, "Draft"), (100000002, "Approved"));

        await _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Approved"
        });

        _captured.Should().BeOfType<SdkDeleteOptionValueRequest>()
            .Which.Value.Should().Be(100000002);
    }

    // ---- update-option alignment (#1170) ----

    [Fact]
    public async Task UpdateOptionValue_ByLabel_AppliesNewLabel() // #1170
    {
        SetupGlobalOptions((100000000, "Draft"), (100000001, "Approved"));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Approved",
            NewLabel = "Accepted"
        });

        var upd = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        upd.Value.Should().Be(100000001);
        upd.Label.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("Accepted");
    }

    [Fact]
    public async Task UpdateOptionValue_ColorOnly_SendsNoLabel_AndCarriesTheColor() // #1170 + #1233
    {
        SetupGlobalOptions((100000000, "Draft"));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 100000000,
            Color = "#FF0000"
        });

        // One request: no Label (an omitted label is left unchanged, observed live), the Color parameter, MergeLabels.
        _client.Verify(c => c.ExecuteAsync(It.IsAny<SdkUpdateOptionValueRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var upd = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        upd.Value.Should().Be(100000000);
        upd.Parameters.ContainsKey("Label").Should().BeFalse();
        upd["Color"].Should().Be("#FF0000");
        upd.MergeLabels.Should().BeTrue();
    }

    [Fact]
    [Trait("AC", "AC-146")]
    public async Task ColorThatDataverseDidNotStore_FailsWithWriteNotConfirmed()
    {
        SetupGlobalOptions((100000000, "Draft"));
        _ignoreColor = true;

        var act = () => _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution", OptionSetName = "new_mystatus", Value = 100000000, Color = "#FF0000"
        });

        var error = (await act.Should().ThrowAsync<PpdsException>()).Which;
        error.ErrorCode.Should().Be(MetadataErrorCodes.WriteNotConfirmed);
        error.Message.Should().Contain("Option 100000000 was updated (color).").And.Contain("did not store its color: requested '#FF0000', stored 'none'");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [Trait("AC", "AC-146")]
    public async Task EmptySolution_IsSentAsNoSolution(string solution)
    {
        SetupGlobalOptions((100000000, "Draft"));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = solution, OptionSetName = "new_mystatus", Value = 100000000, Color = "#FF0000"
        });

        _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Which.SolutionUniqueName.Should().BeNull(
            "Dataverse rejects an empty solution name (observed live)");
    }

    [Fact]
    public async Task UpdateOptionValue_NeitherValueNorLabel_ThrowsMissingRequiredField() // #1170
    {
        var act = () => _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            NewLabel = "Renamed"
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.MissingRequiredField);
    }

    // ---- service-layer value/label mutual exclusivity (Gemini review) ----

    [Fact]
    public async Task UpdateOptionValue_BothValueAndLabel_ThrowsInvalidConstraint()
    {
        var act = () => _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 100000000,
            Label = "Draft",
            NewLabel = "Renamed"
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.InvalidConstraint);
    }

    [Fact]
    public async Task DeleteOptionValue_BothValueAndLabel_ThrowsInvalidConstraint()
    {
        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 100000000,
            Label = "Draft"
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.InvalidConstraint);
    }

    // ---- dry-run early exit (#1172) ----

    [Fact]
    public async Task AddOptionValue_DryRun_DoesNotCallSdk() // #1172
    {
        var assigned = await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Pending",
            Value = 100000005,
            DryRun = true
        });

        assigned.Should().Be(100000005);
        _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateOptionValue_DryRun_ValidatesTargetWithoutMutating() // #1172
    {
        SetupGlobalOptions((100000000, "Draft"));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 100000000,
            NewLabel = "Renamed",
            DryRun = true
        });

        // Resolution ran (validating the target exists) but no mutation was sent.
        _retrieveRequest.Should().NotBeNull();
        _client.Verify(c => c.ExecuteAsync(It.IsAny<SdkUpdateOptionValueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteOptionValue_DryRun_ValidatesTargetWithoutMutating() // #1172
    {
        SetupGlobalOptions((100000000, "Draft"));

        await _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Label = "Draft",
            DryRun = true
        });

        _retrieveRequest.Should().NotBeNull();
        _client.Verify(c => c.ExecuteAsync(It.IsAny<SdkDeleteOptionValueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteOptionValue_DryRun_TargetNotFound_StillThrows() // #1172
    {
        SetupGlobalOptions((100000000, "Draft"));

        var act = () => _service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 42,
            DryRun = true
        });

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ErrorCode == MetadataErrorCodes.OptionNotFound);
    }
}

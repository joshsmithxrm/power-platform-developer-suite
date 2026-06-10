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
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkDeleteOptionValueRequest = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers global option-value targeting by value/label on the authoring service (#1169)
/// — the global counterpart of the local (column) option tests.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataGlobalOptionServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly DataverseMetadataAuthoringService _service;
    private OrganizationRequest? _captured;
    private RetrieveOptionSetRequest? _retrieveRequest;

    public MetadataGlobalOptionServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
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
                    case SdkDeleteOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new DeleteOptionValueResponse());
                    default:
                        _captured = req;
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

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
    public async Task UpdateOptionValue_BothValueAndLabel_ThrowsInvalidConstraint() // review: service-layer mutual exclusivity
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
    public async Task DeleteOptionValue_BothValueAndLabel_ThrowsInvalidConstraint() // review: service-layer mutual exclusivity
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
    public async Task UpdateOptionValue_ColorOnly_PreservesCurrentLabelAndForwardsColor() // #1170
    {
        SetupGlobalOptions((100000000, "Draft"));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Value = 100000000,
            Color = "#FF0000"
        });

        var upd = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        upd.Value.Should().Be(100000000);
        upd.Label.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("Draft");
        upd["Color"].Should().Be("#FF0000");
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
}

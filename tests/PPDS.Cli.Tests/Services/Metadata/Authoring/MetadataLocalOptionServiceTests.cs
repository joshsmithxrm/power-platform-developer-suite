using System;
using System.Collections.Generic;
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
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOptionValueRequest = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers #1161 local (column-scoped) option management on the authoring service (AC-55, AC-56).
/// </summary>
[Trait("Category", "Unit")]
public class MetadataLocalOptionServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly DataverseMetadataAuthoringService _service;
    private OrganizationRequest? _captured;

    public MetadataLocalOptionServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
    }

    /// <summary>Sets up the column's local option set returned by RetrieveAttribute, and captures Insert/Update/Delete.</summary>
    private void SetupColumnOptions(params (int value, string label)[] options)
    {
        var optionSet = new OptionSetMetadata { IsGlobal = false };
        foreach (var (value, label) in options)
            optionSet.Options.Add(new OptionMetadata(new Label(label, 1033), value));
        var picklist = new PicklistAttributeMetadata { OptionSet = optionSet };
        var retrieveResponse = new RetrieveAttributeResponse();
        retrieveResponse.Results["AttributeMetadata"] = picklist;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                switch (req)
                {
                    case RetrieveAttributeRequest:
                        return Task.FromResult<OrganizationResponse>(retrieveResponse);
                    case InsertOptionValueRequest:
                        _captured = req;
                        var ins = new InsertOptionValueResponse();
                        ins.Results["NewOptionValue"] = ((InsertOptionValueRequest)req).Value ?? 0;
                        return Task.FromResult<OrganizationResponse>(ins);
                    case SdkUpdateOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                    case SdkDeleteOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new DeleteOptionValueResponse());
                    default:
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

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
            NewLabel = "Minimal",
            Color = "#00FF00"
        });

        var upd = _captured.Should().BeOfType<SdkUpdateOptionValueRequest>().Subject;
        upd.Value.Should().Be(864630000);
        upd["AttributeLogicalName"].Should().Be("hsl_severity");
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class StateTransitionProcessorTests
{
    private readonly Mock<IDataverseConnectionPool> _pool;
    private readonly Mock<IPooledClient> _client;
    private readonly StateTransitionProcessor _sut;

    public StateTransitionProcessorTests()
    {
        _pool = new Mock<IDataverseConnectionPool>();
        _client = new Mock<IPooledClient>();

        _pool.Setup(p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        _sut = new StateTransitionProcessor(_pool.Object);
    }

    [Fact]
    public async Task AppliesSetStateForNonDefaultState()
    {
        // AC-06: transition statecode=1, record currently at statecode=0
        var recordId = Guid.NewGuid();
        var context = CreateContext();
        context.StateTransitions.Add("incident", recordId, new StateTransitionData
        {
            EntityName = "incident",
            RecordId = recordId,
            StateCode = 1,
            StatusCode = 5,
            SdkMessage = null
        });

        // Record is currently active (statecode=0)
        SetupRetrieveState(recordId, stateCode: 0);

        OrganizationRequest? capturedRequest = null;
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "SetState"),
                It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new OrganizationResponse());

        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestName.Should().Be("SetState");
        var state = (OptionSetValue)capturedRequest["State"];
        state.Value.Should().Be(1);
        var status = (OptionSetValue)capturedRequest["Status"];
        status.Value.Should().Be(5);
    }

    [Fact]
    public async Task SkipsAlreadyInTargetStateRecords()
    {
        // AC-16: record already matches desired statecode and statuscode, skip transition
        var recordId = Guid.NewGuid();
        var context = CreateContext();
        context.StateTransitions.Add("incident", recordId, new StateTransitionData
        {
            EntityName = "incident",
            RecordId = recordId,
            StateCode = 1,
            StatusCode = 5,
            SdkMessage = null
        });

        // Record already has the target statecode and statuscode
        SetupRetrieveState(recordId, stateCode: 1, statusCode: 5);

        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        _client.Verify(
            c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "SetState"),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecutesSdkMessageWhenSpecified()
    {
        var recordId = Guid.NewGuid();
        var closeEntity = new Entity("opportunityclose");
        closeEntity["opportunityid"] = new EntityReference("opportunity", recordId);
        closeEntity["subject"] = "Won (migrated)";

        var context = CreateContext();
        context.StateTransitions.Add("opportunity", recordId, new StateTransitionData
        {
            EntityName = "opportunity",
            RecordId = recordId,
            StateCode = 1,
            StatusCode = 3,
            SdkMessage = "WinOpportunity",
            MessageData = new Dictionary<string, object>
            {
                ["OpportunityClose"] = closeEntity,
                ["Status"] = new OptionSetValue(3)
            }
        });

        SetupRetrieveState(recordId, stateCode: 0);

        OrganizationRequest? capturedRequest = null;
        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "WinOpportunity"),
                It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new OrganizationResponse());

        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestName.Should().Be("WinOpportunity");
        capturedRequest["OpportunityClose"].Should().Be(closeEntity);
        ((OptionSetValue)capturedRequest["Status"]).Value.Should().Be(3);
    }

    [Fact]
    public async Task ReturnsCorrectPhaseResult()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var context = CreateContext();
        context.StateTransitions.Add("incident", id1, new StateTransitionData
        { EntityName = "incident", RecordId = id1, StateCode = 1, StatusCode = 5 });
        context.StateTransitions.Add("incident", id2, new StateTransitionData
        { EntityName = "incident", RecordId = id2, StateCode = 1, StatusCode = 5 });
        context.StateTransitions.Add("account", id3, new StateTransitionData
        { EntityName = "account", RecordId = id3, StateCode = 1, StatusCode = 2 });

        SetupRetrieveState(id1, stateCode: 0);
        SetupRetrieveState(id2, stateCode: 0);
        SetupRetrieveState(id3, stateCode: 0);

        _client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.RequestName == "SetState"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(3);
        result.SuccessCount.Should().Be(3);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task SkipsWhenNoTransitions()
    {
        var context = CreateContext();

        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(0);
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);

        _pool.Verify(
            p => p.GetClientAsync(
                It.IsAny<Dataverse.Client.DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _client.Verify(
            c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #region Helpers

    private void SetupRetrieveState(Guid recordId, int stateCode, int statusCode = 1)
    {
        var entity = new Entity { Id = recordId };
        entity["statecode"] = new OptionSetValue(stateCode);
        entity["statuscode"] = new OptionSetValue(statusCode);
        var response = new RetrieveResponse();
        response["Entity"] = entity;

        _client.Setup(c => c.ExecuteAsync(
                It.Is<RetrieveRequest>(r => r.Target.Id == recordId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private static ImportContext CreateContext()
    {
        var data = new MigrationData();
        var plan = new ExecutionPlan();
        var options = new ImportOptions { ContinueOnError = true };
        var fieldMetadata = new FieldMetadataCollection(
            new Dictionary<string, Dictionary<string, FieldValidity>>());

        return new ImportContext(data, plan, options, new IdMappingCollection(), fieldMetadata);
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
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
        SetupRetrieveStatecode(recordId, 0);

        OrganizationRequest? capturedRequest = null;
        _pool.Setup(p => p.ExecuteAsync(
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
    public async Task SkipsAlreadyClosedRecords()
    {
        // AC-16: record already has non-zero statecode, skip transition
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

        // Record already has non-zero statecode (closed)
        SetupRetrieveStatecode(recordId, 1);

        var result = await _sut.ProcessAsync(context, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SuccessCount.Should().Be(1);
        _pool.Verify(
            p => p.ExecuteAsync(
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

        SetupRetrieveStatecode(recordId, 0);

        OrganizationRequest? capturedRequest = null;
        _pool.Setup(p => p.ExecuteAsync(
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

        SetupRetrieveStatecode(id1, 0);
        SetupRetrieveStatecode(id2, 0);
        SetupRetrieveStatecode(id3, 0);

        _pool.Setup(p => p.ExecuteAsync(
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
        _pool.Verify(
            p => p.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #region Helpers

    private void SetupRetrieveStatecode(Guid recordId, int statecodeValue)
    {
        var entity = new Entity { Id = recordId };
        entity["statecode"] = new OptionSetValue(statecodeValue);

        _client.Setup(c => c.RetrieveAsync(
                It.IsAny<string>(),
                recordId,
                It.IsAny<ColumnSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
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

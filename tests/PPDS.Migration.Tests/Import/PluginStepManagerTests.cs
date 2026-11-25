using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class PluginStepManagerTests
{
    private readonly Mock<IDataverseConnectionPool> _connectionPool;
    private readonly Mock<IPooledClient> _pooledClient;
    private readonly PluginStepManager _sut;

    public PluginStepManagerTests()
    {
        _connectionPool = new Mock<IDataverseConnectionPool>();
        _pooledClient = new Mock<IPooledClient>();

        _connectionPool
            .Setup(x => x.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_pooledClient.Object);

        _sut = new PluginStepManager(_connectionPool.Object);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionPoolIsNull()
    {
        var act = () => new PluginStepManager(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connectionPool");
    }

    #region GetActivePluginStepsAsync (#1921: resolved in the target by logical name)

    [Fact]
    public async Task GetActivePluginStepsAsync_SelectsByTheTargetCode_NotTheSourceCode()
    {
        // The issue's example: in the source new_widget had 10050; in the target 10050 is
        // new_gadget and new_widget is 10051.
        var widgetStep = Guid.NewGuid();
        var gadgetStep = Guid.NewGuid();
        var target = new FakePluginTarget();
        target.Codes["new_widget"] = 10051;
        target.Codes["new_gadget"] = 10050;
        target.Steps.Add((widgetStep, "widget step", 10051));
        target.Steps.Add((gadgetStep, "gadget step", 10050));
        target.Attach(_pooledClient);

        var result = await _sut.GetActivePluginStepsAsync(new[] { "new_widget" });

        result.Select(st => st.Id).Should().Equal(widgetStep);
        result[0].Name.Should().Be("widget step");
        target.MetadataRequests.Should().Equal("new_widget");
        target.FetchXml.Should().ContainSingle().Which.Should().Contain("value='10051'").And.NotContain("10050");
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_TableNotInTarget_RefusesAndQueriesNoSteps()
    {
        var target = new FakePluginTarget();
        target.Codes["new_widget"] = 10051;
        target.Attach(_pooledClient);

        var act = () => _sut.GetActivePluginStepsAsync(new[] { "new_widget", "new_missing" });

        var ex = await act.Should().ThrowAsync<ImportRefusedException>();
        ex.Which.Reason.Should().Be(ImportRefusalReasons.PluginTableNotResolved);
        ex.Which.Message.Should().Contain("new_missing").And.Contain("No plug-in step was disabled");
        target.FetchXml.Should().BeEmpty("nothing is selected when any table cannot be resolved");
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_SelectsOnlyEnabledVisibleCustomSteps()
    {
        // #1921 review MF5: the query's state, hidden and custom-level conditions are each pinned.
        var target = new FakePluginTarget();
        target.Codes["new_widget"] = 10051;
        var custom = Guid.NewGuid();
        target.Steps.Add((custom, "custom step", 10051));
        target.Steps.Add(new FakeStep { Id = Guid.NewGuid(), Name = "already disabled", Code = 10051, Enabled = false });
        target.Steps.Add(new FakeStep { Id = Guid.NewGuid(), Name = "hidden", Code = 10051, Hidden = true });
        target.Steps.Add(new FakeStep { Id = Guid.NewGuid(), Name = "system", Code = 10051, CustomizationLevel = 0 });
        target.Attach(_pooledClient);

        var result = await _sut.GetActivePluginStepsAsync(new[] { "new_widget" });

        result.Select(st => st.Id).Should().Equal(custom);
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_QueryStructure_IsPinned()
    {
        // #1934 r2 MF5: the link must be inner (outer would select every custom step of every table);
        // the step conditions must be ANDed (OR would select already-disabled steps and then switch them
        // on); the table conditions must be ORed (AND would select nothing for two tables).
        var target = new FakePluginTarget();
        target.Codes["new_widget"] = 10051;
        target.Codes["new_gadget"] = 10050;
        target.Attach(_pooledClient);

        await _sut.GetActivePluginStepsAsync(new[] { "new_widget", "new_gadget" });

        var doc = System.Xml.Linq.XDocument.Parse(target.FetchXml.Single());
        var entity = doc.Root!.Elements("entity").Single();
        entity.Attribute("name")!.Value.Should().Be("sdkmessageprocessingstep");

        var stepFilter = entity.Elements("filter").Single();
        stepFilter.Attribute("type")!.Value.Should().Be("and");
        stepFilter.Elements().Should().OnlyContain(e => e.Name.LocalName == "condition");
        stepFilter.Elements("condition").Select(c => ((string)c.Attribute("attribute")!, (string)c.Attribute("operator")!, (string)c.Attribute("value")!))
            .Should().BeEquivalentTo(new[] { ("statecode", "eq", "0"), ("ishidden", "eq", "0"), ("customizationlevel", "eq", "1") });

        var link = entity.Elements("link-entity").Single();
        link.Attribute("name")!.Value.Should().Be("sdkmessagefilter");
        link.Attribute("from")!.Value.Should().Be("sdkmessagefilterid");
        link.Attribute("to")!.Value.Should().Be("sdkmessagefilterid");
        link.Attribute("link-type")!.Value.Should().Be("inner");

        var tableFilter = link.Elements("filter").Single();
        tableFilter.Attribute("type")!.Value.Should().Be("or");
        tableFilter.Elements().Should().OnlyContain(e => e.Name.LocalName == "condition");
        tableFilter.Elements("condition").Select(c => ((string)c.Attribute("attribute")!, (string)c.Attribute("operator")!, (string)c.Attribute("value")!))
            .Should().BeEquivalentTo(new[] { ("primaryobjecttypecode", "eq", "10051"), ("primaryobjecttypecode", "eq", "10050") });
        entity.Descendants("filter").Should().HaveCount(2, "no other filter may narrow or widen the selection");
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_MetadataForAnotherTable_Refuses()
    {
        var target = new FakePluginTarget { MetadataAnswersWith = "new_gadget" };
        target.Codes["new_widget"] = 10051;
        target.Steps.Add((Guid.NewGuid(), "widget step", 10051));
        target.Attach(_pooledClient);

        var act = () => _sut.GetActivePluginStepsAsync(new[] { "new_widget" });

        (await act.Should().ThrowAsync<ImportRefusedException>()).Which.Reason
            .Should().Be(ImportRefusalReasons.PluginTableNotResolved);
        target.FetchXml.Should().BeEmpty();
    }

    public static TheoryData<Exception, string> MetadataFailures => new()
    {
        { new PPDS.Dataverse.Resilience.ServiceProtectionException("c", TimeSpan.FromSeconds(1), 0), "the service is throttling requests" },
        { new TimeoutException(), "the request timed out" },
        { new System.ServiceModel.FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = PluginStepManager.PrivilegeDeniedErrorCode, Message = "Principal user is missing prvReadEntity" }), "you do not have the privilege to read it" },
        { new System.ServiceModel.FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040216), Message = "Generic SQL error" }), "the service returned error 0x80040216" },
    };

    [Theory]
    [MemberData(nameof(MetadataFailures))]
    public async Task GetActivePluginStepsAsync_OtherMetadataFailure_IsNotReportedAsNotFound(Exception failure, string reason)
    {
        // #1921 review should-fix 7.
        _pooledClient
            .Setup(x => x.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var act = () => _sut.GetActivePluginStepsAsync(new[] { "new_widget" });

        var ex = (await act.Should().ThrowAsync<ImportRefusedException>()).Which;
        ex.Reason.Should().Be(ImportRefusalReasons.PluginTableMetadataUnavailable);
        ex.Message.Should().Contain(reason).And.Contain("new_widget").And.NotContain("not found");
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_MetadataWithoutCode_Refuses()
    {
        _pooledClient
            .Setup(x => x.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var response = new Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse();
                response.Results["EntityMetadata"] = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = "new_widget" };
                return response;
            });

        var act = () => _sut.GetActivePluginStepsAsync(new[] { "new_widget" });

        (await act.Should().ThrowAsync<ImportRefusedException>()).Which.Reason
            .Should().Be(ImportRefusalReasons.PluginTableNotResolved);
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_ReadsEveryPage()
    {
        var target = new FakePluginTarget { PageSize = 2 };
        target.Codes["account"] = 1;
        for (var i = 0; i < 5; i++)
        {
            target.Steps.Add((Guid.NewGuid(), $"step {i}", 1));
        }
        target.Attach(_pooledClient);

        var result = await _sut.GetActivePluginStepsAsync(new[] { "account" });

        result.Should().HaveCount(5);
        target.FetchXml.Should().HaveCount(3);
        target.FetchXml[1].Should().Contain("page='2'").And.Contain("paging-cookie=");
        target.FetchXml[0].Should().Contain($"count='{PluginStepManager.PageSize}'");
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_ReturnsEmpty_WhenNoTables()
    {
        var result = await _sut.GetActivePluginStepsAsync(Array.Empty<string>());

        result.Should().BeEmpty();
        _pooledClient.Verify(x => x.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _pooledClient.Verify(x => x.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_ReturnsEmpty_WhenNoStepsFound()
    {
        var target = new FakePluginTarget();
        target.Codes["account"] = 1;
        target.Attach(_pooledClient);

        var result = await _sut.GetActivePluginStepsAsync(new[] { "account" });

        result.Should().BeEmpty();
    }

    #endregion

    #region DisablePluginStepsAsync

    [Fact]
    public async Task DisablePluginStepsAsync_SetsStateToDisabled_AndRecordsTheStep()
    {
        var stepId = Guid.NewGuid();
        var updatedEntities = new List<Entity>();
        var disabled = new List<Guid>();

        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntities.Add(e))
            .Returns(Task.CompletedTask);

        await _sut.DisablePluginStepsAsync(new[] { stepId }, disabled);

        updatedEntities.Should().HaveCount(1);
        updatedEntities[0].Id.Should().Be(stepId);
        updatedEntities[0].LogicalName.Should().Be("sdkmessageprocessingstep");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1, "StateCode should be Disabled (1)");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2, "StatusCode should be Disabled (2)");
        disabled.Should().Equal(stepId);
    }

    [Fact]
    public async Task DisablePluginStepsAsync_DoesNothing_WhenEmptyList()
    {
        await _sut.DisablePluginStepsAsync(Array.Empty<Guid>(), new List<Guid>());

        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisablePluginStepsAsync_StepThatCannotBeDisabled_Refuses_AndKeepsItForReenable()
    {
        // A step left enabled would run during an import that was asked to suppress it.
        var stepId1 = Guid.NewGuid();
        var stepId2 = Guid.NewGuid();
        var stepId3 = Guid.NewGuid();
        var disabled = new List<Guid>();

        _pooledClient
            .SetupSequence(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .ThrowsAsync(new InvalidOperationException("Second step failed"))
            .Returns(Task.CompletedTask);

        var act = () => _sut.DisablePluginStepsAsync(new[] { stepId1, stepId2, stepId3 }, disabled);

        (await act.Should().ThrowAsync<ImportRefusedException>()).Which.Reason
            .Should().Be(ImportRefusalReasons.PluginStepNotDisabled);
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        disabled.Should().Equal(new[] { stepId1, stepId2 }, "the failed update may have been applied, so it is re-enabled too");
    }

    [Fact]
    public async Task DisablePluginStepsAsync_CancelledPartWay_KeepsTheStepsAlreadyDisabled()
    {
        using var cts = new CancellationTokenSource();
        var stepIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var disabled = new List<Guid>();
        var calls = 0;

        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++calls == 2)
                {
                    cts.Cancel();
                }
                return Task.CompletedTask;
            });

        var act = () => _sut.DisablePluginStepsAsync(stepIds, disabled, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        disabled.Should().Equal(stepIds[0], stepIds[1]);
    }

    [Fact]
    public async Task DisablePluginStepsAsync_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.DisablePluginStepsAsync(new[] { Guid.NewGuid() }, new List<Guid>(), cts.Token));
    }

    #endregion

    #region EnablePluginStepsAsync

    [Fact]
    public async Task EnablePluginStepsAsync_SetsStateToEnabled()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var updatedEntities = new List<Entity>();

        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntities.Add(e))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.EnablePluginStepsAsync(new[] { stepId });

        // Assert
        updatedEntities.Should().HaveCount(1);
        updatedEntities[0].Id.Should().Be(stepId);
        updatedEntities[0].LogicalName.Should().Be("sdkmessageprocessingstep");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0, "StateCode should be Enabled (0)");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(1, "StatusCode should be Enabled (1)");
    }

    [Fact]
    public async Task EnablePluginStepsAsync_DoesNothing_WhenEmptyList()
    {
        // Act
        await _sut.EnablePluginStepsAsync(Array.Empty<Guid>());

        // Assert
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnablePluginStepsAsync_ContinuesOnFailure_AndAggregates()
    {
        // Arrange (D5): all steps are attempted even when one fails; the method
        // then throws a PluginStepReenableException summarising every failure.
        var stepId1 = Guid.NewGuid();
        var stepId2 = Guid.NewGuid();

        _pooledClient
            .SetupSequence(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("First step failed"))
            .Returns(Task.CompletedTask);

        // Act
        var act = async () => await _sut.EnablePluginStepsAsync(new[] { stepId1, stepId2 });

        // Assert — all steps attempted and aggregated exception thrown
        var ex = await act.Should().ThrowAsync<PluginStepReenableException>();
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        ex.Which.Failures.Should().HaveCount(1);
        ex.Which.Failures[0].StepId.Should().Be(stepId1);
        ex.Which.Failures[0].Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task EnablePluginStepsAsync_DoesNotThrow_WhenAllStepsSucceed()
    {
        // Arrange — D5 regression guard: the new aggregation path must stay
        // quiet when nothing fails.
        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var stepIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        // Act
        await _sut.EnablePluginStepsAsync(stepIds);

        // Assert — all three updates attempted, no throw
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task EnablePluginStepsAsync_CollectsAllFailures_InExceptionPayload()
    {
        // Arrange — all three steps fail; aggregated exception captures each one.
        var stepIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("token expired"));

        // Act
        var act = async () => await _sut.EnablePluginStepsAsync(stepIds);

        // Assert
        var ex = await act.Should().ThrowAsync<PluginStepReenableException>();
        ex.Which.Failures.Should().HaveCount(3);
        ex.Which.Failures.Select(f => f.StepId).Should().BeEquivalentTo(stepIds);
        ex.Which.Message.Should().Contain("3 plugin step(s)");
    }

    #endregion
}

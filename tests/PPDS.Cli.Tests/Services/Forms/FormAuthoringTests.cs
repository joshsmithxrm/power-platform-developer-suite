using System.ServiceModel;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

[Trait("Category", "Unit")]
public sealed class FormAuthoringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddField_ManagedOrUnmanaged_PreservesTimelineAndPriorDraftWithoutPublishing(bool managed)
    {
        using var h = new Harness { Managed = managed };
        var before = XDocument.Parse(h.Draft);
        var result = await h.Service.AddFieldDraftAsync(h.Request());
        result.Status.Should().Be("saved-unpublished");
        result.DraftVerification.Status.Should().Be("succeeded");
        result.DraftHash.Should().Be(FormService.ComputeDraftHash(h.Draft));
        result.PublicationScope.Should().Be("none");
        var after = XDocument.Parse(h.Draft);
        XNode.DeepEquals(before.Descendants("parameters").Single(), after.Descendants("parameters").Single()).Should().BeTrue();
        after.Root!.Attribute("headerdensity")!.Value.Should().Be("HighWithControls");
        after.Descendants("control").Count(c => (string?)c.Attribute("datafieldname") == "telephone1").Should().Be(1);
        h.Writes.Should().Be(1);
        h.Publications.Should().Be(0);
        h.Published.Should().Be(TimelineFormFixture.Xml);
        h.Queries.Should().OnlyContain(q => q.EntityName != "systemform" ||
            q.Criteria.Conditions.Any(c => c.AttributeName == "objecttypecode" && (string)c.Values[0] == "account"));
    }

    [Fact]
    public async Task RetryWithFreshHash_IsIdempotent_AndDoesNotAssociateTwice()
    {
        using var h = new Harness();
        var first = await h.Service.AddFieldDraftAsync(h.Request() with { SolutionUniqueName = "Fixture" });
        var second = await h.Service.AddFieldDraftAsync(h.Request() with { SolutionUniqueName = "Fixture" });
        first.Status.Should().Be("saved-unpublished");
        second.Status.Should().Be("unchanged");
        h.Writes.Should().Be(1);
        h.Associations.Should().Be(1);
    }

    [Fact]
    public async Task ReadOnly_DryRunPlansWithoutAcknowledgementOrAnyWrites_ActualNoOpRejected()
    {
        using var h = new Harness();
        h.Confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException("Environment.ReadOnly", "read only"));
        var result = await h.Service.AddFieldDraftAsync(h.Request() with { DryRun = true, SolutionUniqueName = "Fixture" });
        result.Status.Should().Be("planned");
        result.DraftWrite.Status.Should().Be("would-change");
        result.SolutionAssociation.Status.Should().Be("would-change");
        h.Confirmation.Verify(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        var actual = () => h.Service.AddFieldDraftAsync(h.Request() with { FieldLogicalName = "name" });
        (await actual.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be("Environment.ReadOnly");
        h.Writes.Should().Be(0);
        h.Associations.Should().Be(0);
        h.Publications.Should().Be(0);
    }

    [Theory]
    [InlineData("locked", "Forms.FormNotCustomizable")]
    [InlineData("stale", "Forms.StaleDraft")]
    [InlineData("solution", "Forms.InvalidSolution")]
    [InlineData("section", "Forms.SectionNotFound")]
    [InlineData("field", "Forms.ColumnNotFound")]
    [InlineData("form", "Forms.FormNotFound")]
    [InlineData("type", "Forms.FormNotFound")]
    public async Task InvalidPreconditions_DoNotWrite(string failure, string errorCode)
    {
        using var h = new Harness();
        var request = h.Request();
        if (failure == "locked") h.Customizable = false;
        if (failure == "stale") request = request with { ExpectedDraftHash = new string('0', 64) };
        if (failure == "solution") { h.ManagedSolution = true; request = request with { SolutionUniqueName = "Managed" }; }
        if (failure == "section") request = request with { SectionId = Guid.NewGuid() };
        if (failure == "field") request = request with { FieldLogicalName = "absent" };
        if (failure == "form") h.MissingForm = true;
        if (failure == "type") h.FormType = 7;
        var act = () => h.Service.AddFieldDraftAsync(request);
        var error = (await act.Should().ThrowAsync<PpdsException>()).Which;
        error.ErrorCode.Should().Be(errorCode);
        h.Writes.Should().Be(0);
        h.Associations.Should().Be(0);
        h.Publications.Should().Be(0);
    }

    [Fact]
    public async Task AssociationFailure_ReturnsSavedDraftAndFailedAssociation_NoPublish()
    {
        using var h = new Harness { AssociationFailure = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = "denied" }) };
        var result = await h.Service.AddFieldDraftAsync(h.Request() with { SolutionUniqueName = "Fixture" });
        result.Success.Should().BeFalse();
        result.Status.Should().Be("partial");
        result.DraftWrite.Status.Should().Be("succeeded");
        result.SolutionAssociation.Status.Should().Be("failed");
        result.DraftVerification.Status.Should().Be("not-attempted");
        h.Draft.Should().Contain("telephone1");
        h.Publications.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureOrCancellationAfterSubmission_ReportsUnknownOutcome(bool cancelled)
    {
        using var h = new Harness { WriteFailure = cancelled ? new OperationCanceledException() : new IOException("connection lost") };
        var result = await h.Service.AddFieldDraftAsync(h.Request());
        result.Status.Should().Be("partial");
        result.DraftWrite.Status.Should().Be("unknown");
        result.DraftVerification.Status.Should().Be("not-attempted");
        result.DraftWrite.ErrorCode.Should().Be(cancelled ? "Operation.Cancelled" : "Forms.SubmissionFailed");
        h.Publications.Should().Be(0);
    }

    [Fact]
    public async Task ServerIgnoringDraftWrite_ReportsVerificationFailure()
    {
        using var h = new Harness { IgnoreWrite = true };
        var result = await h.Service.AddFieldDraftAsync(h.Request());
        result.Status.Should().Be("partial");
        result.DraftWrite.Status.Should().Be("succeeded");
        result.DraftVerification.Status.Should().Be("failed");
        result.DraftVerification.ErrorCode.Should().Be("Forms.VerificationFailed");
    }

    [Fact]
    public async Task SupportedRowVersion_UsesSdkConcurrencyCondition()
    {
        using var h = new Harness { Optimistic = true };
        var result = await h.Service.AddFieldDraftAsync(h.Request());
        result.ConcurrencyMode.Should().Be("row-version");
        h.UpdateRequest.Should().NotBeNull();
        h.UpdateRequest!.ConcurrencyBehavior.Should().Be(ConcurrencyBehavior.IfRowVersionMatches);
        h.UpdateRequest.Target.RowVersion.Should().Be("42");
        h.Client.Verify(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentRequests_SerializeAndRejectSecondStaleDraft()
    {
        using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeWrite = async () => { entered.SetResult(); await release.Task; };
        var request = h.Request();
        var first = h.Service.AddFieldDraftAsync(request);
        await entered.Task;
        var second = h.Service.AddFieldDraftAsync(request);
        second.IsCompleted.Should().BeFalse();
        release.SetResult();
        (await first).Status.Should().Be("saved-unpublished");
        var act = () => second;
        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be("Forms.StaleDraft");
        h.Writes.Should().Be(1);
    }

    [Fact]
    public async Task Publication_RequiresExplicitEntityAcknowledgement_AndVerifiesPublishedState()
    {
        using var h = new Harness();
        await h.Service.AddFieldDraftAsync(h.Request());
        var request = new PublishFormEntityRequest("account", Harness.FormId, FormService.ComputeDraftHash(h.Draft), "contact");
        var act = () => h.Service.PublishFormEntityAsync(request);
        await act.Should().ThrowAsync<PpdsValidationException>();
        h.Publications.Should().Be(0);
        var result = await h.Service.PublishFormEntityAsync(request with { ConfirmPublishEntity = "account" });
        result.Status.Should().Be("published");
        result.PublicationScope.Should().Be("entity");
        result.PublishedVerification.Status.Should().Be("succeeded");
        h.PublishXml.Should().Contain("<entity>account</entity>");
        h.PublishXml.Should().NotContain("PublishAll");
        h.Published.Should().Be(h.Draft);
    }

    [Fact]
    public async Task PublicationReadbackFailure_ReportsPublishedButUnverified()
    {
        using var h = new Harness { IgnorePublish = true };
        await h.Service.AddFieldDraftAsync(h.Request());
        var result = await h.Service.PublishFormEntityAsync(new("account", Harness.FormId, FormService.ComputeDraftHash(h.Draft), "account"));
        result.Status.Should().Be("partial");
        result.EntityPublication.Status.Should().Be("succeeded");
        result.PublishedVerification.Status.Should().Be("failed");
    }

    [Fact]
    public async Task CancelledBeforePlanning_PropagatesCancellation_AndDoesNotAcquireClient()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => h.Service.AddFieldDraftAsync(h.Request() with { DryRun = true }, ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        h.Pool.Verify(c => c.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class Harness : IDisposable
    {
        public static readonly Guid FormId = new("11111111-1111-1111-1111-111111111111");
        public readonly Mock<IDataverseConnectionPool> Pool = new();
        public readonly Mock<IPooledClient> Client = new();
        public readonly Mock<IEnvironmentConfirmationService> Confirmation = new();
        private readonly FormMutationCoordinator _coordinator = new();
        public FormService Service { get; }
        public string Draft = TimelineFormFixture.Xml;
        public string Published = TimelineFormFixture.Xml;
        public bool Managed, ManagedSolution, Optimistic, IgnoreWrite, IgnorePublish, Associated, MissingForm;
        public int FormType = 2;
        public bool Customizable = true;
        public int Writes, Associations, Publications;
        public string? PublishXml;
        public Exception? WriteFailure, AssociationFailure;
        public Func<Task>? BeforeWrite;
        public UpdateRequest? UpdateRequest;
        public readonly List<QueryExpression> Queries = [];
        public Harness()
        {
            Pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(Client.Object);
            Client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Returns<Entity, CancellationToken>(async (entity, _) => await Write(entity));
            Client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>(async (request, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    switch (request)
                    {
                        case RetrieveUnpublishedMultipleRequest read:
                            Queries.Add((QueryExpression)read.Query);
                            return new RetrieveUnpublishedMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = Forms(Draft) } };
                        case RetrieveEntityRequest:
                            var entityMetadata = new EntityMetadata();
                            typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.IsOptimisticConcurrencyEnabled))!.SetValue(entityMetadata, Optimistic);
                            return new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = entityMetadata } };
                        case UpdateRequest update:
                            UpdateRequest = update; await Write(update.Target); return new UpdateResponse();
                        case AddSolutionComponentRequest:
                            if (AssociationFailure is not null) throw AssociationFailure;
                            Associations++; Associated = true; return new AddSolutionComponentResponse();
                        case PublishXmlRequest publish:
                            Publications++; PublishXml = publish.ParameterXml; if (!IgnorePublish) Published = Draft;
                            return new PublishXmlResponse();
                        default: throw new InvalidOperationException(request.RequestName);
                    }
                });
            Client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns<QueryBase, CancellationToken>((query, _) =>
                {
                    var q = (QueryExpression)query; Queries.Add(q);
                    return Task.FromResult(q.EntityName switch
                    {
                        "systemform" => Forms(Published),
                        "solution" => new EntityCollection([new Entity("solution", Guid.NewGuid()) { ["ismanaged"] = ManagedSolution }]),
                        "solutioncomponent" => new EntityCollection(Associated ? [new Entity("solutioncomponent", Guid.NewGuid())] : []),
                        _ => throw new InvalidOperationException(q.EntityName)
                    });
                });
            var metadata = new Mock<IMetadataQueryService>();
            metadata.Setup(m => m.GetAttributesAsync("account", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { "name", "telephone1" }.Select(n => new AttributeMetadataDto { LogicalName = n, SchemaName = n, DisplayName = n, AttributeType = "String" }).ToArray());
            Confirmation.SetupGet(c => c.Context).Returns(new EnvironmentConfirmationContext("test-session", "test-identity", "https://forms.example.test"));
            Confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Service = new FormService(Pool.Object, metadata.Object, NullLogger<FormService>.Instance, Confirmation.Object, _coordinator);
        }
        public AddFieldDraftRequest Request() => new("account", FormId, new("44444444-4444-4444-4444-444444444444"), "telephone1", FormService.ComputeDraftHash(Draft));
        private EntityCollection Forms(string xml) => MissingForm ? new() : new([new Entity("systemform", FormId) { RowVersion = "42", ["formid"] = FormId,
            ["formxml"] = xml, ["name"] = "Fixture", ["type"] = new OptionSetValue(FormType), ["ismanaged"] = Managed,
            ["iscustomizable"] = new BooleanManagedProperty(Customizable) }]);
        private async Task Write(Entity entity)
        {
            if (BeforeWrite is not null) await BeforeWrite();
            Writes++;
            if (!IgnoreWrite) Draft = (string)entity["formxml"];
            if (WriteFailure is not null) throw WriteFailure;
        }
        public void Dispose() => _coordinator.Dispose();
    }
}

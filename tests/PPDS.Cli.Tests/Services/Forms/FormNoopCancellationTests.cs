using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

// Identical cancellation tests are frozen on the v2 product tree and corrected successor.
[Trait("Category", "Unit")]
[Trait("AC", "AC-64")]
public sealed class FormNoopCancellationTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("strict")]
    [InlineData("move")]
    public async Task NoopReporterCancellation_IsNotReportedAsSuccessfulCompletion(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token);
        var reporter = new CancelingReporter("Unchanged", cancellation.Cancel);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "add" => h.Service.AddFieldAsync(new("account", "Fixture", "General", ["NAME"]), reporter, cancellation.Token),
            "strict" => h.Service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["name"]), reporter, cancellation.Token),
            _ => h.Service.MoveFieldAsync(new("account", "Fixture", "name", "General", "General"), reporter, cancellation.Token)
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(reporter.Triggered); Assert.Empty(h.Writes);
        Assert.Equal(0, h.SolutionRequests); Assert.Equal(0, h.PublishRequests);
        Assert.Equal(h.Original, h.StateXml); h.AssertIsolation();
    }

    [Fact]
    public async Task Add_MetadataReturningAfterCancellation_CannotCompleteAnExistingFieldNoop()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var xml = ContributorFormFixture.Build("base");
        var id = new Guid("12345678-1234-1234-1234-123456789abc");
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(request => request is RetrieveUnpublishedMultipleRequest), token))
            .ReturnsAsync(new RetrieveUnpublishedMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection([
                new Entity("systemform", id) { ["formid"] = id, ["name"] = "Fixture", ["type"] = new OptionSetValue(2),
                    ["ismanaged"] = false, ["iscustomizable"] = new BooleanManagedProperty(true), ["formxml"] = xml }]) } });
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, token)).ReturnsAsync(client.Object);
        var metadata = new Mock<IMetadataQueryService>(MockBehavior.Strict);
        metadata.Setup(m => m.GetAttributesAsync("account", null, token)).Returns(() =>
        {
            cancellation.Cancel(); // A collaborator can finish without throwing after its token is canceled.
            return Task.FromResult<IReadOnlyList<AttributeMetadataDto>>([new() { LogicalName = "name", SchemaName = "Name", DisplayName = "Name", AttributeType = "String" }]);
        });
        var confirmation = new Mock<IEnvironmentConfirmationService>(MockBehavior.Strict);
        confirmation.Setup(c => c.EnsureConfirmedAsync("forms mutation", null, token)).Returns(Task.CompletedTask);
        var service = new FormService(pool.Object, metadata.Object, NullLogger<FormService>.Instance, confirmation.Object);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AddFieldAsync(new("account", "Fixture", "General", ["NAME"]), ct: token));
        Assert.Equal(token, error.CancellationToken);
        pool.Verify(p => p.GetClientAsync(null, null, token), Times.Once);
        client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), token), Times.Once); client.Verify(c => c.DisposeAsync(), Times.Once);
        metadata.Verify(m => m.GetAttributesAsync("account", null, token), Times.Once);
        confirmation.Verify(c => c.EnsureConfirmedAsync("forms mutation", null, token), Times.Once);
        pool.VerifyNoOtherCalls(); client.VerifyNoOtherCalls(); metadata.VerifyNoOtherCalls(); confirmation.VerifyNoOtherCalls();
        FormXmlValidator.Validate(XDocument.Parse(xml));
    }

    private sealed class CancelingReporter(string phase, Action cancel) : IProgressReporter
    {
        internal bool Triggered { get; private set; }
        public void ReportPhase(string actual, string? detail = null) { if (actual == phase) { Triggered = true; cancel(); } }
        public void ReportProgress(ProgressSnapshot snapshot) { }
        public void ReportWarning(string message) { }
        public void ReportInfo(string message) { }
    }
}

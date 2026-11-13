using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public sealed class PluginInvocationSourceVerificationTests
{
    [Theory]
    [InlineData("Parent", 0)]
    [InlineData("Child", 1)]
    [InlineData("Internal", -1)]
    public async Task ExplicitInvocationSource_PersistedValueSucceedsAfterFreshRead(string requested, int stored)
    {
        var f = new Fixture(stored);
        using var cancellation = new CancellationTokenSource();

        await f.Service.UpdateStepAsync(f.StepId, new StepUpdateRequest(InvocationSource: requested), cancellation.Token);

        f.AssertSingleWriteAndRead(cancellation.Token);
        Assert.Equal(stored, Assert.Single(f.Updates).GetAttributeValue<OptionSetValue>("invocationsource").Value);
        Assert.Equal(new[] { "lease", "admit", "dispose", "lease", "update", "dispose", "lease", "verify", "dispose" }, f.Events);
    }

    [Fact]
    public async Task ExplicitInvocationSource_RetainedValueFailsWithAcknowledgement()
    {
        var f = new Fixture(stored: 1);
        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Service.UpdateStepAsync(f.StepId,
            new StepUpdateRequest(Description: "synthetic-private-description", InvocationSource: "Parent")));

        Assert.Equal(ErrorCodes.Plugin.UpdateNotPersisted, error.ErrorCode);
        Assert.Equal(0, error.Context!["expected"]);
        Assert.Equal(1, error.Context["observed"]);
        Assert.Equal("mismatch", error.Context["verification"]);
        Assert.DoesNotContain("synthetic-private-description", error.UserMessage);
        Assert.Contains("No retry or rollback", error.UserMessage);
        AssertAcknowledged(error, f.StepId);
        f.AssertSingleWriteAndRead(CancellationToken.None);
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("wrong-row")]
    [InlineData("wrong-table")]
    [InlineData("missing-field")]
    [InlineData("null-field")]
    [InlineData("wrong-type")]
    [InlineData("unknown-option")]
    public async Task ExplicitInvocationSource_InvalidReadbackIsUnverified(string variant)
    {
        var f = new Fixture(stored: 0);
        switch (variant)
        {
            case "missing-row": f.Stored = null; break;
            case "wrong-row": f.Stored!.Id = Guid.NewGuid(); break;
            case "wrong-table": f.Stored!.LogicalName = "account"; break;
            case "missing-field": f.Stored!.Attributes.Remove("invocationsource"); break;
            case "null-field": f.Stored!["invocationsource"] = null; break;
            case "wrong-type": f.Stored!["invocationsource"] = 0; break;
            case "unknown-option": f.Stored!["invocationsource"] = new OptionSetValue(99); break;
        }

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Update());

        Assert.Equal(ErrorCodes.Plugin.VerificationFailed, error.ErrorCode);
        Assert.Equal("unverified", error.Context!["verification"]);
        AssertAcknowledged(error, f.StepId);
        f.AssertSingleWriteAndRead(CancellationToken.None);
    }

    [Fact]
    public async Task ExplicitInvocationSource_ReadFailureIsSafeAndRetainsAcknowledgement()
    {
        var f = new Fixture(stored: 0);
        var cause = new InvalidOperationException("PRIVATE-SERVER-DIAGNOSTIC");
        f.ReadFailure = cause;

        var error = await Assert.ThrowsAsync<PpdsException>(() => f.Update());

        Assert.Equal(ErrorCodes.Plugin.VerificationFailed, error.ErrorCode);
        Assert.Same(cause, error.InnerException);
        Assert.DoesNotContain("PRIVATE-SERVER-DIAGNOSTIC", error.UserMessage);
        Assert.DoesNotContain("PRIVATE-SERVER-DIAGNOSTIC", ExceptionMapper.Map(error).Message);
        AssertAcknowledged(error, f.StepId);
        f.AssertSingleWriteAndRead(CancellationToken.None);
    }

    [Theory]
    [InlineData("before-read", 0)]
    [InlineData("during-read", 1)]
    [InlineData("after-read", 1)]
    public async Task ExplicitInvocationSource_CancellationRetainsTokenAndAcknowledgement(string phase, int expectedReads)
    {
        var f = new Fixture(stored: 0);
        using var cancellation = new CancellationTokenSource();
        if (phase == "before-read") f.AfterWrite = cancellation.Cancel;
        if (phase == "during-read") f.ReadFailure = new OperationCanceledException("PRIVATE-CANCELLATION", cancellation.Token);
        if (phase == "after-read") f.AfterRead = cancellation.Cancel;

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => f.Update(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Contains("persistence remains unverified", error.Message);
        Assert.DoesNotContain("PRIVATE-CANCELLATION", error.Message);
        AssertAcknowledged(error, f.StepId);
        Assert.Single(f.Updates);
        Assert.Equal(expectedReads, f.VerificationReads);
        f.AssertNoOtherMutations();
    }

    [Fact]
    public async Task OmittedInvocationSource_DoesNotAddVerification()
    {
        var f = new Fixture(stored: 1);

        await f.Service.UpdateStepAsync(f.StepId, new StepUpdateRequest(Description: "changed"));

        Assert.False(Assert.Single(f.Updates).Contains("invocationsource"));
        Assert.Equal(0, f.VerificationReads);
        Assert.Equal(new[] { "lease", "admit", "dispose", "lease", "update", "dispose" }, f.Events);
        f.AssertNoOtherMutations();
    }

    [Fact]
    public async Task InvocationSource_WriteFailureIsUnknownAndNeverReadOrRetried()
    {
        var f = new Fixture(stored: 0) { WriteFailure = new TimeoutException("synthetic write timeout") };

        var error = await Assert.ThrowsAsync<TimeoutException>(() => f.Update());

        var outcome = Assert.Single(PluginMutationJournal.Read(error));
        Assert.Equal("unknown", outcome.Outcome);
        Assert.Equal(f.StepId, outcome.RecordId);
        Assert.Single(f.Updates);
        Assert.Equal(0, f.VerificationReads);
        f.AssertNoOtherMutations();
    }

    private static void AssertAcknowledged(Exception exception, Guid id)
    {
        var outcome = Assert.Single(PluginMutationJournal.Read(exception));
        Assert.Equal("acknowledged", outcome.Outcome);
        Assert.Equal("sdkmessageprocessingstep", outcome.Entity);
        Assert.Equal("update", outcome.Operation);
        Assert.Equal("row", outcome.Phase);
        Assert.Equal(id, outcome.RecordId);
    }

    private sealed class Fixture
    {
        private readonly Mock<IPooledClient> _client = new(MockBehavior.Strict);
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        internal Guid StepId { get; } = Guid.NewGuid();
        internal PluginRegistrationService Service { get; }
        internal Entity? Stored { get; set; }
        internal Exception? ReadFailure { get; set; }
        internal Exception? WriteFailure { get; set; }
        internal Action? AfterWrite { get; set; }
        internal Action? AfterRead { get; set; }
        internal List<string> Events { get; } = [];
        internal List<Entity> Updates { get; } = [];
        internal int VerificationReads { get; private set; }
        private CancellationToken _readToken;

        internal Fixture(int stored)
        {
            Stored = new Entity("sdkmessageprocessingstep", StepId) { ["invocationsource"] = new OptionSetValue(stored) };
            var admission = new Entity("sdkmessageprocessingstep", StepId)
            {
                ["name"] = "Synthetic step", ["ismanaged"] = false,
                ["iscustomizable"] = new BooleanManagedProperty(true), ["stage"] = new OptionSetValue(40)
            };
            _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .Returns((QueryBase _, CancellationToken _) =>
                {
                    Events.Add("admit");
                    return Task.FromResult(new EntityCollection([admission]));
                });
            _client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Returns((Entity entity, CancellationToken _) =>
                {
                    Events.Add("update"); Updates.Add(entity); AfterWrite?.Invoke();
                    return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
                });
            _client.Setup(c => c.RetrieveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
                .Returns((string table, Guid id, ColumnSet columns, CancellationToken token) =>
                {
                    Events.Add("verify"); VerificationReads++; _readToken = token;
                    Assert.Equal("sdkmessageprocessingstep", table);
                    Assert.Equal(StepId, id);
                    Assert.False(columns.AllColumns);
                    Assert.Equal("invocationsource", Assert.Single(columns.Columns));
                    AfterRead?.Invoke();
                    return ReadFailure is null ? Task.FromResult(Stored!) : Task.FromException<Entity>(ReadFailure);
                });
            _client.Setup(c => c.DisposeAsync()).Returns(() => { Events.Add("dispose"); return ValueTask.CompletedTask; });
            _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns(() => { Events.Add("lease"); return Task.FromResult(_client.Object); });
            Service = new PluginRegistrationService(_pool.Object, new InactiveFakeShakedownGuard(), NullLogger<PluginRegistrationService>.Instance);
        }

        internal Task Update(CancellationToken token = default) =>
            Service.UpdateStepAsync(StepId, new StepUpdateRequest(InvocationSource: "Parent"), token);

        internal void AssertSingleWriteAndRead(CancellationToken token)
        {
            Assert.Single(Updates);
            Assert.Equal(1, VerificationReads);
            Assert.Equal(token, _readToken);
            _client.Verify(c => c.UpdateAsync(It.Is<Entity>(e => e.Id == StepId && e.LogicalName == "sdkmessageprocessingstep"), token), Times.Once);
            _client.Verify(c => c.DisposeAsync(), Times.Exactly(3));
            AssertNoOtherMutations();
        }

        internal void AssertNoOtherMutations()
        {
            _client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
            _client.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
            _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}

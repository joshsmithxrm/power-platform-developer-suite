using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Models;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

// Fixed-only additive API tests. They are not included in the B3 before-run inventory.
[Trait("Category", "Unit")]
[Trait("AC", "AC-66")]
public sealed class FormAddOutcomeContractTests
{
    [Fact]
    public async Task RealOutcome_DistinguishesAddsExistingFieldsAndDuplicateInputs()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token);
        h.AssertPreconditions("base");
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General",
            ["NAME", ContributorFormFixture.Priority, "MAYO_PRIORITYSCORE"]), ct: cancellation.Token);
        Assert.True(result.CountsKnown);
        Assert.Equal(new[] { ContributorFormFixture.Priority }, result.AddedFields);
        Assert.Equal(new[] { "name" }, result.AlreadyPresentFields);
        Assert.Equal(new[] { "MAYO_PRIORITYSCORE" }, result.DuplicateRequests);
        Assert.True(result.FormXmlChanged);
        Assert.False(result.SolutionAssociationRequested);
        Assert.False(result.PublicationRequested);
        Assert.Single(h.Writes);
        h.AssertIsolation();
    }

    [Fact]
    public async Task OldTaskAndNewOutcome_ShareOneMutationPathAndRetryState()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token);
        h.AssertPreconditions("base");
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", [ContributorFormFixture.Priority]), ct: cancellation.Token);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["MAYO_PRIORITYSCORE"]), ct: cancellation.Token);
        Assert.True(result.CountsKnown);
        Assert.Empty(result.AddedFields!);
        Assert.Equal(new[] { ContributorFormFixture.Priority }, result.AlreadyPresentFields);
        Assert.False(result.FormXmlChanged);
        Assert.Single(h.Writes);
        h.AssertIsolation();
    }

    [Fact]
    public async Task ActualLegacyOnlyImplementation_DefaultCallsOldSignatureOnceWithUnknownCounts()
    {
        var legacy = new LegacyOnlyFormService();
        IFormService service = legacy;
        using var cancellation = new CancellationTokenSource();
        var request = new AddFieldRequest("account", "Fixture", "General", ["name"], "fixture_solution", true);
        var result = await service.AddFieldsWithResultAsync(request, ct: cancellation.Token);
        Assert.Equal(1, legacy.Calls);
        Assert.Same(request, legacy.Request);
        Assert.Equal(cancellation.Token, legacy.Token);
        Assert.False(result.CountsKnown);
        Assert.Null(result.AddedFields); Assert.Null(result.AlreadyPresentFields); Assert.Null(result.DuplicateRequests);
        Assert.Null(result.FormXmlChanged);
        Assert.True(result.SolutionAssociationRequested); Assert.True(result.PublicationRequested);
        Assert.Null(typeof(LegacyOnlyFormService).GetMethod(nameof(IFormService.AddFieldsWithResultAsync)));
        var old = typeof(IFormService).GetMethod(nameof(IFormService.AddFieldAsync),
            [typeof(AddFieldRequest), typeof(IProgressReporter), typeof(CancellationToken)]);
        Assert.NotNull(old);
        Assert.Equal(typeof(Task), old.ReturnType);
        Assert.Equal(new[] { "request", "reporter", "ct" }, old.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task ActualLegacyOnlyImplementation_PropagatesItsOriginalFailure()
    {
        var expected = new PpdsException("Fixture.LegacyFailure", "Synthetic legacy failure.");
        var legacy = new LegacyOnlyFormService { Failure = expected };
        IFormService service = legacy;
        var actual = await Assert.ThrowsAsync<PpdsException>(() => service.AddFieldsWithResultAsync(
            new("account", "Fixture", "General", ["name"])));
        Assert.Same(expected, actual);
        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public async Task LegacyImplementation_NewLayoutApisNeverFallBackToDestructiveLegacyReorderOrAdd()
    {
        var legacy = new LegacyOnlyFormService(); IFormService service = legacy;
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["name"])));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.MoveFieldAsync(new("account", "Fixture", "name", "General", "Other")));
        Assert.Equal(0, legacy.Calls);
    }
}

/// <summary>An actual implementation of only the pre-existing interface, with no outcome override or Moq default dispatch.</summary>
internal sealed class LegacyOnlyFormService : IFormService
{
    internal int Calls { get; private set; }
    internal AddFieldRequest? Request { get; private set; }
    internal CancellationToken Token { get; private set; }
    internal Exception? Failure { get; init; }
    public Task AddFieldAsync(AddFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default)
    {
        Calls++; Request = request; Token = ct;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
    public Task<ListResult<FormInfo>> ListAsync(string entityLogicalName, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<FormDetail?> GetAsync(string entityLogicalName, string formName, bool unpublished = false, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<FormAuthoringResult> AddFieldDraftAsync(AddFieldDraftRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<FormAuthoringResult> PublishFormEntityAsync(PublishFormEntityRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetFormXmlAsync(SetFormXmlRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TabResult> AddTabAsync(AddTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task UpdateTabAsync(UpdateTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveTabAsync(RemoveTabRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TabInfo?> FindTabAsync(FindTabRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SectionResult> AddSectionAsync(AddSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task UpdateSectionAsync(UpdateSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveSectionAsync(RemoveSectionRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SectionInfo?> FindSectionAsync(FindSectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveFieldAsync(RemoveFieldRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReorderFieldsAsync(ReorderFieldsRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AddSubgridAsync(AddSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveSubgridAsync(RemoveSubgridRequest request, IProgressReporter? reporter = null, CancellationToken ct = default) => throw new NotSupportedException();
}

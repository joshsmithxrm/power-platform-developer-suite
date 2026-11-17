using System.ServiceModel;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkUpdateOption = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;
using SdkDeleteOption = Microsoft.Xrm.Sdk.Messages.DeleteOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// #1535: every path that takes a global choice name from the caller resolves the stored name. By default the
/// fake server is case-sensitive (the field report's observed behaviour) and rejects an unknown spelling with
/// the "option set not found" fault; <see cref="GlobalChoiceServerFake.CaseInsensitive"/> models the alternative.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GlobalChoiceNameResolutionTests
{
    internal const string Stored = "mayo_airequesttype";
    internal const string Typed = "mayo_AIRequestType";

    public static TheoryData<string> ResolvingOperations => new()
    {
        "choice-update", "choice-delete-preview", "option-add", "option-add-color",
        "option-update", "option-update-color", "option-delete", "option-reorder"
    };

    public static TheoryData<string> AllOperations
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var operation in ResolvingOperations) data.Add(operation);
            data.Add("choice-delete");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllOperations))]
    [Trait("AC", "AC-133")]
    public async Task ExactStoredName_SendsTheSameNames_WithoutCatalogListing(string operation)
    {
        using var f = new GlobalChoiceServerFake();
        await f.Run(operation, Stored);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Empty(f.Faults);
        Assert.All(f.NamedRequests(), name => Assert.Equal(Stored, name));
        f.AssertLeases();
    }

    [Theory]
    [MemberData(nameof(ResolvingOperations))]
    [Trait("AC", "AC-133")]
    public async Task CaseVariant_ResolvesToStoredName_ForEveryRequestAfterTheRejectedAttempt(string operation)
    {
        using var f = new GlobalChoiceServerFake();
        await f.Run(operation, Typed);
        var listing = Assert.IsType<RetrieveAllOptionSetsRequest>(Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest));
        Assert.True(listing.RetrieveAsIfPublished);
        // Exactly one rejected attempt under the caller's spelling. For add-option and reorder that attempt is the
        // write itself (rejected as "not found", so nothing was written); every other path rejects a read.
        Assert.Equal(Typed, Assert.Single(f.Faults));
        Assert.Equal(Typed, GlobalChoiceServerFake.NameOf(f.Requests[0]));
        var afterListing = f.Requests.SkipWhile(r => r is not RetrieveAllOptionSetsRequest).Skip(1).ToList();
        Assert.NotEmpty(afterListing);
        Assert.All(afterListing.Select(GlobalChoiceServerFake.NameOf).Where(n => n != null), name => Assert.Equal(Stored, name));
        if (operation != "choice-delete-preview") Assert.Contains(afterListing, GlobalChoiceServerFake.IsWrite);
        f.AssertLeases();
    }

    [Theory]
    [InlineData("option-update")]
    [InlineData("option-update-color")]
    [InlineData("choice-update")]
    [InlineData("choice-delete-preview")]
    [Trait("AC", "AC-133")]
    public async Task CaseInsensitiveServer_AdoptsTheStoredNameItReturns_WithoutListing(string operation)
    {
        using var f = new GlobalChoiceServerFake { CaseInsensitive = true };
        await f.Run(operation, Typed);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Empty(f.Faults);
        Assert.Equal(Typed, GlobalChoiceServerFake.NameOf(f.Requests[0])); // first read uses the caller's spelling
        Assert.All(f.Requests.Skip(1).Select(GlobalChoiceServerFake.NameOf).Where(n => n != null), name => Assert.Equal(Stored, name));
        f.AssertLeases();
    }

    [Theory]
    [InlineData("choice-update")]
    [InlineData("option-add")]
    [InlineData("option-delete")]
    [InlineData("option-reorder")]
    [Trait("AC", "AC-133")]
    public async Task AmbiguousCaseVariants_FailWithAmbiguityCarryingTheOriginalFault(string operation)
    {
        using var f = new GlobalChoiceServerFake { Catalog = [GlobalChoiceServerFake.Choice("MAYO_AIREQUESTTYPE"), GlobalChoiceServerFake.Choice("mayo_airequestTYPE")] };
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.Run(operation, Typed));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.IsType<FaultException<OrganizationServiceFault>>(ex.InnerException);
        Assert.Single(f.Faults);
        Assert.DoesNotContain(f.Requests.SkipWhile(r => r is not RetrieveAllOptionSetsRequest), GlobalChoiceServerFake.IsWrite);
        f.AssertLeases();
    }

    public static TheoryData<string, Type, string?> MissingOutcomes => new()
    {
        // Pre-existing outcome for a name that does not exist, preserved exactly (type and code).
        { "choice-update", typeof(FaultException<OrganizationServiceFault>), null },
        { "choice-delete", typeof(PpdsException), MetadataErrorCodes.SdkOperationFailed },
        { "choice-delete-preview", typeof(PpdsException), MetadataErrorCodes.SdkOperationFailed },
        { "option-add", typeof(FaultException<OrganizationServiceFault>), null },
        { "option-add-color", typeof(FaultException<OrganizationServiceFault>), null },
        { "option-update", typeof(PpdsException), MetadataErrorCodes.EntityNotFound },
        { "option-update-color", typeof(PpdsException), MetadataErrorCodes.EntityNotFound },
        { "option-delete", typeof(PpdsException), MetadataErrorCodes.EntityNotFound },
        { "option-reorder", typeof(FaultException<OrganizationServiceFault>), null }
    };

    [Theory]
    [MemberData(nameof(MissingOutcomes))]
    [Trait("AC", "AC-134")]
    public async Task MissingName_KeepsThePreviousFailure_AndDoesNotRetry(string operation, Type expectedType, string? expectedCode)
    {
        using var f = new GlobalChoiceServerFake();
        var ex = await Record.ExceptionAsync(() => f.Run(operation, "mayo_Missing"));
        Assert.IsType(expectedType, ex);
        if (expectedCode != null)
        {
            Assert.Equal(expectedCode, Assert.IsAssignableFrom<PpdsException>(ex).ErrorCode);
            Assert.IsType<FaultException<OrganizationServiceFault>>(ex!.InnerException);
        }
        Assert.Equal("mayo_Missing", Assert.Single(f.Faults));
        Assert.Equal(operation == "choice-delete" ? 0 : 1, f.Requests.Count(r => r is RetrieveAllOptionSetsRequest));
        Assert.DoesNotContain(f.Requests, r => r is DeleteOptionSetRequest);
        f.AssertLeases();
    }

    [Theory]
    [InlineData("choice-update", Stored)]
    [InlineData("choice-delete-preview", Stored)]
    [InlineData("option-add", Stored)]
    [InlineData("option-update", Stored)]
    [InlineData("option-reorder", Stored)]
    [InlineData("option-add", Typed)]
    [InlineData("option-reorder", Typed)]
    [InlineData("option-delete", Typed)]
    [Trait("AC", "AC-134")]
    public async Task OtherServerFault_PropagatesWithoutListingOrRetry(string operation, string name)
    {
        // A privilege fault: a fault without the anchored not-found phrase is never a reason to list the catalog
        // or re-send under another spelling. Near-miss "Could not find" texts are covered separately.
        using var f = new GlobalChoiceServerFake { FailFirstNamedRequestWith = GlobalChoiceServerFake.PrivilegeFault };
        var ex = await Record.ExceptionAsync(() => f.Run(operation, name));
        Assert.NotNull(ex);
        Assert.Same(f.InjectedFault, ex is FaultException<OrganizationServiceFault> ? ex : ex!.InnerException);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Single(f.Requests, r => GlobalChoiceServerFake.NameOf(r) != null);
        f.AssertLeases();
    }

    [Theory]
    [InlineData("Could not find solution with name SharedOptionSets")]                       // not about an option set
    [InlineData("Could not find an optionset with name mayo_other")]                          // about a different name
    [InlineData("Could not find option value 5 in optionset mayo_AIRequestType")]             // names the set, wrong phrase
    [InlineData("Could not find publisher for solution SharedOptionSet_mayo_AIRequestType")]  // echoed identifiers only
    [Trait("AC", "AC-134")]
    public async Task NearMissNotFoundFault_PropagatesWithoutListingOrRetry(string message)
    {
        // The text fallback only applies to faults that do not carry the ObjectDoesNotExist code.
        using var f = new GlobalChoiceServerFake { FailFirstNamedRequestWith = () => GlobalChoiceServerFake.Fault(message, 0x80040203) };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run("option-add", Typed));
        Assert.Same(f.InjectedFault, ex);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Single(f.Requests, r => GlobalChoiceServerFake.NameOf(r) != null);
    }

    [Theory]
    [MemberData(nameof(ResolvingOperations))]
    [Trait("AC", "AC-133")]
    public async Task NonEnglishNotFoundFault_ResolvesByFaultCode(string operation)
    {
        // #1535 on a non-English organisation: the fault text matches none of the English phrases, but the
        // ObjectDoesNotExist code identifies it.
        using var f = new GlobalChoiceServerFake { NotFoundTemplate = "Impossible de trouver un groupe d'options nommé {0}" };
        await f.Run(operation, Typed);
        Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Equal(Typed, Assert.Single(f.Faults));
        var afterListing = f.Requests.SkipWhile(r => r is not RetrieveAllOptionSetsRequest).Skip(1).ToList();
        Assert.All(afterListing.Select(GlobalChoiceServerFake.NameOf).Where(n => n != null), name => Assert.Equal(Stored, name));
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task NonEnglishFaultWithAnotherCode_PropagatesWithoutListingOrRetry()
    {
        using var f = new GlobalChoiceServerFake { FailFirstNamedRequestWith = () => GlobalChoiceServerFake.Fault("Le privilège prvWriteOptionSet est manquant.", 0x80040220) };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run("option-add", Typed));
        Assert.Same(f.InjectedFault, ex);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task ObjectDoesNotExistAboutAnotherObject_RetriesOnceAndSurfacesTheRetryFailure()
    {
        // The code cannot say WHICH object was missing (here: the solution). The catalog lookup still requires a
        // single stored case variant, the not-found write wrote nothing, and the one retry's own failure surfaces.
        using var f = new GlobalChoiceServerFake
        {
            FailFirstNamedRequestWith = () => GlobalChoiceServerFake.Fault("Could not find solution with name Sol"),
            FailStoredNameWith = () => GlobalChoiceServerFake.Fault("Could not find solution with name Sol")
        };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run("option-add", Typed));
        Assert.Same(f.InjectedFault, ex);
        Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Equal(new[] { Typed, Stored }, f.Requests.OfType<InsertOptionValueRequest>().Select(r => r.OptionSetName));
        f.AssertLeases();
    }

    [Theory]
    [InlineData("option-add")]
    [InlineData("option-reorder")]
    [Trait("AC", "AC-134")]
    public async Task ObjectDoesNotExist_ForTheExactStoredName_ListsOnce_AndNeverResendsTheWrite(string operation)
    {
        // The requested spelling is itself stored, so no other spelling exists to retry: exactly one write, one
        // listing, and the original fault.
        using var f = new GlobalChoiceServerFake { FailFirstNamedRequestWith = () => GlobalChoiceServerFake.Fault("Could not find solution with name Sol") };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run(operation, Stored));
        Assert.Same(f.InjectedFault, ex);
        Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Single(f.Requests, GlobalChoiceServerFake.IsWrite);
        f.AssertLeases();
    }

    public static TheoryData<string, string> NearMissesWithObjectDoesNotExist
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var operation in new[] { "option-add", "option-reorder" })
                foreach (var message in new[]
                {
                    "Could not find solution with name SharedOptionSets",
                    "Could not find an optionset with name mayo_other",
                    "Could not find option value 5 in optionset mayo_AIRequestType",
                    "Could not find publisher for solution SharedOptionSet_mayo_AIRequestType"
                })
                    data.Add(operation, message);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NearMissesWithObjectDoesNotExist))]
    [Trait("AC", "AC-134")]
    public async Task NearMissText_WithObjectDoesNotExist_ListsOnce_AndRetriesOnceUnderTheStoredName(string operation, string message)
    {
        // The code decides, not the text: one listing, one retry under the single stored case variant, and the
        // retry's own outcome stands (here it succeeds; FailingRetry_SurfacesTheRetryFailure covers failure).
        using var f = new GlobalChoiceServerFake { FailFirstNamedRequestWith = () => GlobalChoiceServerFake.Fault(message) };
        await f.Run(operation, Typed);
        Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Equal(new[] { Typed, Stored }, f.Requests.Where(GlobalChoiceServerFake.IsWrite).Select(GlobalChoiceServerFake.NameOf));
        f.AssertLeases();
    }

    [Theory]
    [InlineData("Could not find an optionset with name {0}")]      // recorded server wording
    [InlineData("Could not find an option set with name '{0}'.")]  // spaced variant
    [InlineData("Could not find an option set with specified name")] // wording quoted in #1535; echoes no name
    [Trait("AC", "AC-133")]
    public async Task RecognisedNotFoundWordings_TriggerResolution(string template)
    {
        // English fallback: the wording alone is recognised when the fault carries another code.
        using var f = new GlobalChoiceServerFake { NotFoundTemplate = template, NotFoundCode = 0x80040203 };
        await f.Run("option-add", Typed);
        Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Equal(new[] { Typed, Stored }, f.Requests.OfType<InsertOptionValueRequest>().Select(r => r.OptionSetName));
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task NonFaultWriteFailure_IsNotRetriedUnderAnotherSpelling()
    {
        // A timeout may hide a committed insert, so it must never trigger resolution and a second insert.
        var timeout = new TimeoutException("Synthetic timeout");
        using var f = new GlobalChoiceServerFake { InsertFailure = timeout };
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => f.Run("option-add", Typed));
        Assert.Same(timeout, ex);
        Assert.Single(f.Requests, r => r is InsertOptionValueRequest);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task FailingRetry_SurfacesTheRetryFailure()
    {
        using var f = new GlobalChoiceServerFake { FailStoredNameWith = GlobalChoiceServerFake.PrivilegeFault };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run("option-add", Typed));
        Assert.Same(f.InjectedFault, ex);
        Assert.Equal(new[] { Typed, Stored }, f.Requests.OfType<InsertOptionValueRequest>().Select(r => r.OptionSetName));
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task CatalogLookupFailure_SurfacesTheOriginalFault()
    {
        using var f = new GlobalChoiceServerFake { CatalogFailure = _ => new TimeoutException("Synthetic catalog timeout") };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run("option-add", Typed));
        Assert.Contains("Could not find", ex.Message);
        Assert.Single(f.Requests, r => r is InsertOptionValueRequest);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task NonCallerCancellationDuringListing_KeepsTheOriginalFault()
    {
        using var f = new GlobalChoiceServerFake { CatalogFailure = _ => new TaskCanceledException("Synthetic HTTP-layer timeout") };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Run("option-add", Typed));
        Assert.Contains("Could not find", ex.Message);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task CallerCancellationDuringListing_Propagates()
    {
        using var f = new GlobalChoiceServerFake();
        f.CatalogFailure = fake => { fake.Cancel(); return new OperationCanceledException(fake.Token); };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run("option-add", Typed));
        Assert.Equal(f.Token, ex.CancellationToken);
        Assert.Single(f.Requests, r => r is InsertOptionValueRequest);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task OptionTargetPath_KeepsItsWrappingForNonResolutionFailures()
    {
        using var f = new GlobalChoiceServerFake { CatalogFailure = _ => new TimeoutException("Synthetic catalog timeout") };
        var ex = await Assert.ThrowsAsync<PpdsException>(() => f.Run("option-update", Typed));
        Assert.Equal(MetadataErrorCodes.EntityNotFound, ex.ErrorCode);
        Assert.IsType<FaultException<OrganizationServiceFault>>(ex.InnerException);
    }

    [Theory]
    [InlineData("choice-update")]
    [InlineData("option-add")]
    [Trait("AC", "AC-134")]
    public async Task InputOnlyDryRuns_PerformNoReads(string operation)
    {
        using var f = new GlobalChoiceServerFake();
        if (operation == "choice-update")
            await f.Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = Typed, DisplayName = "X", DryRun = true }, ct: f.Token);
        else
            Assert.Equal(0, await f.Service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = Typed, Label = "New", DryRun = true }, f.Token));
        Assert.Empty(f.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-139")]
    public async Task ServiceDelete_PerformsNoCatalogResolution_AndSendsTheNameItWasGiven(bool caseInsensitiveServer)
    {
        // The service never lists the catalog or substitutes a name on delete. Whether a case variant matches is
        // then up to the server, exactly as before #1535: a case-sensitive server rejects it; a server that
        // accepts it passes the recheck and receives the delete under the caller's spelling.
        using var f = new GlobalChoiceServerFake { CaseInsensitive = caseInsensitiveServer };
        var ex = await Record.ExceptionAsync(() => f.Run("choice-delete", Typed));
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.All(f.NamedRequests(), name => Assert.Equal(Typed, name));
        if (caseInsensitiveServer)
        {
            Assert.Null(ex);
            Assert.Equal(Typed, Assert.Single(f.Requests.OfType<DeleteOptionSetRequest>()).Name);
        }
        else
        {
            Assert.Equal(MetadataErrorCodes.SdkOperationFailed, Assert.IsType<PpdsException>(ex).ErrorCode);
            Assert.DoesNotContain(f.Requests, r => r is DeleteOptionSetRequest);
        }
    }

    [Theory]
    [InlineData("option-update")]
    [InlineData("option-delete")]
    [Trait("AC", "AC-140")]
    public async Task GlobalLabelTarget_PublishedAndDraftDisagree_IsRefusedAndNothingIsWritten(string operation)
    {
        // Review scenario: published 1="Open", 2="Closed"; drafts relabel 1="Closed", 2="Done".
        using var f = new GlobalChoiceServerFake { Draft = [(1, "Closed"), (2, "Done")], Published = [(1, "Open"), (2, "Closed")] };
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.RunByLabel(operation, Stored, "Closed"));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.Contains("--value", ex.Message);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or SdkDeleteOption);
        Assert.Equal(new[] { true, false }, f.Requests.OfType<RetrieveOptionSetRequest>().Select(r => r.RetrieveAsIfPublished));
        f.AssertLeases();
    }

    [Theory]
    [InlineData("option-update", "Open", new[] { 1 }, new[] { 1, 2 })]   // label on 1 in drafts, on 1 and 2 in published
    [InlineData("option-delete", "Open", new int[0], new[] { 1 })]       // label only in the published view
    [Trait("AC", "AC-140")]
    public async Task GlobalLabelTarget_OtherDisagreements_AreRefused(string operation, string label, int[] draftValues, int[] publishedValues)
    {
        using var f = new GlobalChoiceServerFake
        {
            Draft = [.. new[] { 1, 2, 3 }.Select(v => (v, draftValues.Contains(v) ? label : "Other" + v))],
            Published = [.. new[] { 1, 2, 3 }.Select(v => (v, publishedValues.Contains(v) ? label : "Other" + v))]
        };
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.RunByLabel(operation, Stored, label));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or SdkDeleteOption);
    }

    [Theory]
    [InlineData("option-update")]
    [InlineData("option-delete")]
    [Trait("AC", "AC-140")]
    public async Task GlobalLabelTarget_AgreeingOrDraftOnly_Proceeds(string operation)
    {
        using var f = new GlobalChoiceServerFake { Draft = [(1, "Same"), (2, "Fresh")], Published = [(1, "Same"), (2, "Stale")] };
        await f.RunByLabel(operation, Stored, "Same");
        await f.RunByLabel(operation, Stored, "Fresh");
        var values = operation == "option-delete"
            ? f.Requests.OfType<SdkDeleteOption>().Select(r => r.Value)
            : f.Requests.OfType<SdkUpdateOption>().Select(r => r.Value);
        Assert.Equal(new[] { 1, 2 }, values);
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task ServiceDelete_ExpectedIdMismatch_RefusesWithoutDeleting()
    {
        using var f = new GlobalChoiceServerFake();
        var ex = await Assert.ThrowsAsync<MetadataValidationException>(() => f.Service.DeleteGlobalChoiceAsync(
            new DeleteGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = Stored, ExpectedMetadataId = Guid.NewGuid() }, ct: f.Token));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, ex.ErrorCode);
        Assert.DoesNotContain(f.Requests, r => r is DeleteOptionSetRequest);
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task PreviewReportsTheStoredName()
    {
        using var f = new GlobalChoiceServerFake();
        var preview = await f.Service.PreviewDeleteAsync(new MetadataDeleteTarget(MetadataDeleteKind.GlobalChoice, "Sol", Name: Typed), f.Token);
        Assert.True(preview.IsClear);
        Assert.Equal(Stored, preview.TargetName);
        Assert.Equal(GlobalChoiceServerFake.ChoiceId, preview.TargetObjectId);
    }

    [Theory]
    [InlineData(Stored)]
    [InlineData(Typed)]
    [Trait("AC", "AC-141")]
    public async Task ChoiceUpdate_ReadsDraft_SoUnrequestedPropertiesKeepTheirUnpublishedValues(string name)
    {
        // The description was edited and not yet published. 'optionset update --display-name' must not revert it
        // by re-sending the published copy.
        using var f = new GlobalChoiceServerFake
        {
            DraftDescription = new Label("Unpublished description", 1033),
            PublishedDescription = new Label("Published description", 1033)
        };
        await f.Run("choice-update", name);
        Assert.All(f.Requests.OfType<RetrieveOptionSetRequest>(), r => Assert.True(r.RetrieveAsIfPublished));
        var update = Assert.Single(f.Requests.OfType<UpdateOptionSetRequest>());
        Assert.Equal(Stored, update.OptionSet.Name);
        Assert.Equal("Unpublished description", Assert.Single(update.OptionSet.Description.LocalizedLabels).Label);
        Assert.Equal("Renamed", Assert.Single(update.OptionSet.DisplayName.LocalizedLabels).Label);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-135")]
    public async Task AddOptionWithColor_CarriesTheColorOnTheInsert_AndReadsTheDraftBack()
    {
        // The inserted option exists only in draft metadata. A published read (the previous behaviour) could not
        // find it. The colour rides on the insert itself (observed live to be stored) and the as-if-published
        // read-back under the stored name confirms it; nothing else is written.
        using var f = new GlobalChoiceServerFake();
        await f.Run("option-add-color", Stored);
        var insert = Assert.Single(f.Requests.OfType<InsertOptionValueRequest>());
        Assert.Equal("#112233", insert["Color"]);
        Assert.Equal("Sol", insert.SolutionUniqueName);
        var colorRead = f.Requests.OfType<RetrieveOptionSetRequest>().Single();
        Assert.True(colorRead.RetrieveAsIfPublished);
        Assert.Equal(Stored, colorRead.Name);
        Assert.DoesNotContain(f.Requests, r => r is SdkUpdateOption or UpdateOptionSetRequest);
    }
}

/// <summary>#1535 S1: the CLI deletes exactly the global choice it previewed and asked the user to confirm.</summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class GlobalChoiceDeleteCommandTests
{
    private const string Stored = GlobalChoiceNameResolutionTests.Stored;
    private const string Typed = GlobalChoiceNameResolutionTests.Typed;

    [Fact]
    [Trait("AC", "AC-139")]
    public void ConfirmationHelper_NamesTheStoredName_AndSaysTheTypedNameResolvedToIt()
    {
        var (warning, phrase) = OptionSetCommand.BuildDeleteConfirmation(Typed, Stored);
        Assert.Equal($"DELETE OPTIONSET {Stored}", phrase);
        Assert.Contains($"'{Stored}'", warning);
        Assert.Contains($"'{Typed}' resolved to this stored name", warning);
        Assert.Equal($"DELETE OPTIONSET {Stored}", OptionSetCommand.BuildDeleteConfirmation(Stored, Stored).Confirmation);
        Assert.DoesNotContain("resolved", OptionSetCommand.BuildDeleteConfirmation(Stored, Stored).Warning);
        Assert.Null(OptionSetCommand.DescribeNameResolution(Stored, Stored));
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task CaseVariant_Json_ReportsStoredName_AndDeletesExactlyThePreviewedObject()
    {
        using var f = new GlobalChoiceServerFake();
        var (exit, stdout, stderr) = await Invoke(f, json: true);
        Assert.True(exit == 0, stdout + stderr);
        Assert.Equal(Stored, Assert.Single(f.Requests.OfType<DeleteOptionSetRequest>()).Name);
        // One case-insensitive resolution (the preview); the recheck before delete reads the exact stored name.
        Assert.Single(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        Assert.Equal(new[] { Typed, Stored, Stored }, f.Requests.OfType<RetrieveOptionSetRequest>().Select(r => r.Name));
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(Stored, data.GetProperty("name").GetString());
        Assert.Equal(Typed, data.GetProperty("requestedName").GetString());
        Assert.True(data.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task CaseVariant_Text_StderrSaysTheTypedNameResolved()
    {
        using var f = new GlobalChoiceServerFake();
        var (exit, stdout, stderr) = await Invoke(f, json: false);
        Assert.Equal(0, exit);
        Assert.Empty(stdout);
        Assert.Contains($"Global choice '{Typed}' resolved to stored name '{Stored}'.", stderr);
        Assert.Contains($"Global option set '{Stored}' deleted successfully.", stderr);
    }

    [Theory]
    [InlineData(false)] // case-sensitive server: the stored spelling no longer exists
    [InlineData(true)]  // case-insensitive server: the old spelling now reads a different object
    [Trait("AC", "AC-139")]
    public async Task RenamedOrRecreatedBetweenPreviewAndDelete_IsRefused_AndNothingIsDeleted(bool caseInsensitive)
    {
        using var f = new GlobalChoiceServerFake { CaseInsensitive = caseInsensitive };
        // After the preview's dependency read, the stored choice is replaced by a case variant with a new id.
        f.AfterFirstDependencyRead = fake => fake.Catalog = [GlobalChoiceServerFake.Choice("MAYO_AIREQUESTTYPE", Guid.NewGuid())];
        var (exit, stdout, _) = await Invoke(f, json: true);
        Assert.NotEqual(0, exit);
        Assert.DoesNotContain(f.Requests, r => r is DeleteOptionSetRequest);
        Assert.True(f.Requests.Count(r => r is RetrieveAllOptionSetsRequest) <= 1); // no second resolution
        Assert.Equal(Stored, f.Requests.OfType<RetrieveOptionSetRequest>().Last().Name); // recheck reads the exact stored name
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task CaseVariant_DryRun_ReportsStoredAndRequestedNames_AndSendsNoDelete()
    {
        using var f = new GlobalChoiceServerFake();
        var (exit, stdout, stderr) = await Invoke(f, json: true, mode: "--dry-run");
        Assert.True(exit == 0, stdout + stderr);
        Assert.DoesNotContain(f.Requests, r => r is DeleteOptionSetRequest);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(Stored, data.GetProperty("name").GetString());
        Assert.Equal(Typed, data.GetProperty("requestedName").GetString());
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal(Stored, data.GetProperty("preview").GetProperty("targetName").GetString());
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task InteractivePrompt_ShowsAndRequiresTheStoredName_ThenDeletesIt()
    {
        // Drives the real prompt path through the stdin-redirection seam: the user must type the stored name.
        using var f = new GlobalChoiceServerFake();
        var (exit, stdout, stderr) = await Invoke(f, json: false, mode: null, input: $"DELETE OPTIONSET {Stored}\n");
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains($"WARNING: This will permanently delete global option set '{Stored}'. ('{Typed}' resolved to this stored name.)", stderr);
        Assert.Contains($"Type 'DELETE OPTIONSET {Stored}' to confirm", stderr);
        Assert.DoesNotContain($"DELETE OPTIONSET {Typed}", stderr);
        Assert.Equal(Stored, Assert.Single(f.Requests.OfType<DeleteOptionSetRequest>()).Name);
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task InteractivePrompt_TypedNamePhrase_IsNotAccepted_AndNothingIsDeleted()
    {
        using var f = new GlobalChoiceServerFake();
        var (exit, _, stderr) = await Invoke(f, json: false, mode: null, input: $"DELETE OPTIONSET {Typed}\n");
        Assert.Equal(0, exit);
        Assert.Contains("Cancelled.", stderr);
        Assert.DoesNotContain(f.Requests, r => r is DeleteOptionSetRequest);
    }

    [Fact]
    [Trait("AC", "AC-139")]
    public async Task ClearPreviewWithoutTargetId_IsRefused_AndNoDeleteIsSent()
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        service.As<IMetadataDeletePreviewService>()
            .Setup(s => s.PreviewDeleteAsync(It.IsAny<MetadataDeleteTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataDeletePreviewResult { Status = "no-blocking-dependencies", TargetName = Stored });
        var (exit, stdout, _) = await Invoke(service.Object, json: true, mode: "--force", token: CancellationToken.None);
        Assert.Equal(ExitCodes.Failure, exit);
        service.Verify(s => s.DeleteGlobalChoiceAsync(It.IsAny<DeleteGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()), Times.Never);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    private static Task<(int Exit, string Stdout, string Stderr)> Invoke(GlobalChoiceServerFake f, bool json, string? mode = "--force", string? input = null)
        => Invoke(f.Service, json, mode, f.Token, input);

    private static async Task<(int Exit, string Stdout, string Stderr)> Invoke(IMetadataAuthoringService service, bool json, string? mode, CancellationToken token, string? input = null)
    {
        Task<ServiceProvider> Provider(CancellationToken _) => Task.FromResult(new ServiceCollection()
            .AddSingleton(service)
            .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
            .BuildServiceProvider());
        var args = new List<string> { "optionset", "delete", "--solution", "Sol", "--name", Typed };
        if (mode != null) args.Add(mode);
        if (json) args.AddRange(["--output-format", "json"]);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        using var stdin = input != null ? new StringReader(input) : null;
        var oldOut = Console.Out; var oldError = Console.Error; var oldIn = Console.In;
        var oldRedirected = OptionSetCommand.IsInputRedirected;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            if (stdin != null)
            {
                Console.SetIn(stdin);
                OptionSetCommand.IsInputRedirected = () => false;
            }
            var parsed = MetadataCommandGroup.Create(Provider).Parse(args.ToArray());
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync(cancellationToken: token);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut); Console.SetError(oldError); Console.SetIn(oldIn);
            OptionSetCommand.IsInputRedirected = oldRedirected;
        }
    }
}

/// <summary>Synthetic Dataverse metadata endpoint for global choice name resolution.</summary>
internal sealed class GlobalChoiceServerFake : IDisposable
{
    internal static readonly Guid ChoiceId = Guid.Parse("12121212-1212-1212-1212-121212121212");
    private readonly CancellationTokenSource _cts = new();
    private int _active, _acquired, _released, _peak, _dependencyReads;
    private bool _inserted, _firstNamedFailed;
    internal CancellationToken Token => _cts.Token;
    internal void Cancel() => _cts.Cancel();
    internal List<OrganizationRequest> Requests { get; } = [];
    internal List<string> Faults { get; } = [];
    internal OptionSetMetadataBase[] Catalog { get; set; } = [Choice(GlobalChoiceNameResolutionTests.Stored), new BooleanOptionSetMetadata { Name = "mayo_flag", IsGlobal = true }];
    internal bool CaseInsensitive { get; init; }
    internal Exception? InsertFailure { get; init; }
    internal Func<GlobalChoiceServerFake, Exception>? CatalogFailure { get; set; }
    internal Func<Exception>? FailFirstNamedRequestWith { get; init; }
    internal Func<Exception>? FailStoredNameWith { get; init; }
    internal Action<GlobalChoiceServerFake>? AfterFirstDependencyRead { get; set; }
    internal Exception? InjectedFault { get; private set; }
    internal DataverseMetadataAuthoringService Service { get; }

    /// <summary>Not-found message template; the default is the recorded server wording.</summary>
    internal string NotFoundTemplate { get; init; } = "Could not find an optionset with name {0}";
    internal List<(int Value, string Label)>? Draft { get; init; }
    internal List<(int Value, string Label)>? Published { get; init; }

    /// <summary>Choice-level description in the draft (as-if-published) and published reads.</summary>
    internal Label? DraftDescription { get; init; }
    internal Label? PublishedDescription { get; init; }

    /// <summary>Not-found fault code; the default is ObjectDoesNotExist.</summary>
    internal uint NotFoundCode { get; init; } = 0x80040217;

    internal FaultException<OrganizationServiceFault> NotFoundFault(string name) =>
        (FaultException<OrganizationServiceFault>)Fault(string.Format(System.Globalization.CultureInfo.InvariantCulture, NotFoundTemplate, name), NotFoundCode);

    internal static Exception Fault(string message, uint code = 0x80040217) => new FaultException<OrganizationServiceFault>(
        new OrganizationServiceFault { ErrorCode = unchecked((int)code), Message = message }, message);

    internal static Exception PrivilegeFault() => Fault("Principal user is missing prvWriteOptionSet privilege.", 0x80040220);

    internal GlobalChoiceServerFake()
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            _active++; _acquired++; _peak = Math.Max(_peak, _active);
            var released = false;
            var client = new Mock<IPooledClient>(MockBehavior.Strict);
            client.Setup(c => c.DisposeAsync()).Returns(() =>
            {
                Assert.False(released); released = true; _active--; _released++;
                return ValueTask.CompletedTask;
            });
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                {
                    Assert.False(released);
                    Requests.Add(request);
                    return Task.FromResult(Execute(request));
                });
            return client.Object;
        });
        Service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
    }

    internal static OptionSetMetadata Choice(string name, Guid? id = null)
    {
        var choice = new OptionSetMetadata { Name = name, IsGlobal = true, OptionSetType = OptionSetType.Picklist, MetadataId = id ?? ChoiceId };
        choice.Options.Add(new OptionMetadata(new Label("One", 1033), 1));
        choice.Options.Add(new OptionMetadata(new Label("Two", 1033), 2));
        return choice;
    }

    private OrganizationResponse Execute(OrganizationRequest request)
    {
        if (request is RetrieveAllOptionSetsRequest)
        {
            if (CatalogFailure != null) throw CatalogFailure(this);
            return new RetrieveAllOptionSetsResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = Catalog } };
        }
        if (request is RetrieveDependenciesForDeleteRequest dependencies)
        {
            Assert.Equal(9, dependencies.ComponentType);
            if (++_dependencyReads == 1) AfterFirstDependencyRead?.Invoke(this);
            return new RetrieveDependenciesForDeleteResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection() } };
        }
        if (request is InsertOptionValueRequest && InsertFailure != null) throw InsertFailure;

        var name = NameOf(request) ?? throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName);
        if (FailFirstNamedRequestWith != null && !_firstNamedFailed)
        {
            _firstNamedFailed = true;
            throw InjectedFault = FailFirstNamedRequestWith();
        }
        if (FailStoredNameWith != null && name == GlobalChoiceNameResolutionTests.Stored)
            throw InjectedFault = FailStoredNameWith();

        var comparison = CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var match = Catalog.OfType<OptionSetMetadata>().FirstOrDefault(c => string.Equals(c.Name, name, comparison));
        if (match == null)
        {
            Faults.Add(name);
            throw NotFoundFault(name);
        }

        return request switch
        {
            RetrieveOptionSetRequest r => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = Read(match, r.RetrieveAsIfPublished) } },
            InsertOptionValueRequest insert => Insert(insert),
            SdkUpdateOption update => StoreColor(update.Value, update),
            SdkDeleteOption => new DeleteOptionValueResponse(),
            OrderOptionRequest => new OrderOptionResponse(),
            UpdateOptionSetRequest => new UpdateOptionSetResponse(),
            DeleteOptionSetRequest => new DeleteOptionSetResponse(),
            _ => throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName)
        };
    }

    private InsertOptionValueResponse Insert(InsertOptionValueRequest insert)
    {
        _inserted = true;
        StoreColor(3, insert);
        return new InsertOptionValueResponse { Results = new ParameterCollection { ["NewOptionValue"] = 3 } };
    }

    /// <summary>Dataverse stores the Color parameter of an option-value message on the option (observed live).</summary>
    private UpdateOptionValueResponse StoreColor(int value, OrganizationRequest request)
    {
        if (request.Parameters.TryGetValue("Color", out var color)) _colors[value] = (string)color;
        return new UpdateOptionValueResponse();
    }

    private readonly Dictionary<int, string> _colors = [];

    // The inserted option is unpublished: only an as-if-published read sees it.
    private OptionSetMetadata Read(OptionSetMetadata stored, bool asIfPublished)
    {
        var choice = Choice(stored.Name, stored.MetadataId);
        if ((asIfPublished ? DraftDescription : PublishedDescription) is { } description)
            choice.Description = description;
        if ((asIfPublished ? Draft : Published) is { } labels)
        {
            choice.Options.Clear();
            foreach (var (value, label) in labels) choice.Options.Add(new OptionMetadata(new Label(label, 1033), value));
        }
        if (_inserted && asIfPublished) choice.Options.Add(new OptionMetadata(new Label("New", 1033), 3));
        foreach (var option in choice.Options)
            option.Color = option.Value is { } v ? _colors.GetValueOrDefault(v) : null;
        return choice;
    }

    internal static string? NameOf(OrganizationRequest request) => request switch
    {
        RetrieveOptionSetRequest r => r.Name,
        InsertOptionValueRequest r => r.OptionSetName,
        SdkUpdateOption r => r.OptionSetName,
        SdkDeleteOption r => r.OptionSetName,
        OrderOptionRequest r => r.OptionSetName,
        UpdateOptionSetRequest r => r.OptionSet.Name,
        DeleteOptionSetRequest r => r.Name,
        _ => null
    };

    internal static bool IsWrite(OrganizationRequest request)
        => request is InsertOptionValueRequest or SdkUpdateOption or SdkDeleteOption or OrderOptionRequest
            or UpdateOptionSetRequest or DeleteOptionSetRequest;

    internal IEnumerable<string> NamedRequests() => Requests.Select(NameOf).Where(n => n != null)!;

    internal Task Run(string operation, string name) => operation switch
    {
        "choice-update" => Service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = name, DisplayName = "Renamed" }, ct: Token),
        "choice-delete" => Service.DeleteGlobalChoiceAsync(new DeleteGlobalChoiceRequest { SolutionUniqueName = "Sol", Name = name }, ct: Token),
        "choice-delete-preview" => Service.PreviewDeleteAsync(new MetadataDeleteTarget(MetadataDeleteKind.GlobalChoice, "Sol", Name: name), Token),
        "option-add" => Service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = name, Label = "New", SolutionUniqueName = "Sol" }, Token),
        "option-add-color" => Service.AddOptionValueAsync(new AddOptionValueRequest { OptionSetName = name, Label = "New", Color = "#112233", SolutionUniqueName = "Sol" }, Token),
        "option-update" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Value = 1, NewLabel = "Uno", SolutionUniqueName = "Sol" }, Token),
        "option-update-color" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Label = "Two", Color = "#445566", SolutionUniqueName = "Sol" }, Token),
        "option-delete" => Service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest { OptionSetName = name, Value = 2, SolutionUniqueName = "Sol" }, Token),
        "option-reorder" => Service.ReorderOptionsAsync(new PPDS.Dataverse.Metadata.Authoring.ReorderOptionsRequest { OptionSetName = name, Order = [2, 1], SolutionUniqueName = "Sol" }, Token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal Task RunByLabel(string operation, string name, string label) => operation switch
    {
        "option-update" => Service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = name, Label = label, NewLabel = "Renamed", SolutionUniqueName = "Sol" }, Token),
        "option-delete" => Service.DeleteOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.DeleteOptionValueRequest { OptionSetName = name, Label = label, SolutionUniqueName = "Sol" }, Token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal void AssertLeases()
    {
        Assert.Equal(0, _active);
        Assert.Equal(_acquired, _released);
        Assert.True(_peak <= 1); // No pooled client is held across the resolution lookup.
    }

    public void Dispose() => _cts.Dispose();
}

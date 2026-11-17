using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata;

/// <summary>#1535: 'optionset get' resolves a case variant of a stored global choice name.</summary>
[Trait("Category", "Unit")]
public sealed class DataverseMetadataQueryServiceOptionSetNameTests
{
    private const string Stored = "mayo_airequesttype";

    [Fact]
    [Trait("AC", "AC-133")]
    public async Task ExactName_RetrievesOnce_WithoutListing()
    {
        var f = new Fixture();
        var result = await f.Service.GetOptionSetAsync(Stored);
        Assert.Equal(Stored, result.Name);
        Assert.Equal(new[] { Stored }, f.Requests.OfType<RetrieveOptionSetRequest>().Select(r => r.Name));
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-133")]
    public async Task CaseVariant_ResolvesToStoredName()
    {
        var f = new Fixture();
        var result = await f.Service.GetOptionSetAsync("mayo_AIRequestType");
        Assert.Equal(Stored, result.Name);
        Assert.Equal(new[] { "mayo_AIRequestType", Stored }, f.Requests.OfType<RetrieveOptionSetRequest>().Select(r => r.Name));
        // Reads show published state, like 'optionsets' listing.
        Assert.False(Assert.Single(f.Requests.OfType<RetrieveAllOptionSetsRequest>()).RetrieveAsIfPublished);
        Assert.All(f.Requests.OfType<RetrieveOptionSetRequest>(), r => Assert.False(r.RetrieveAsIfPublished));
        f.AssertLeases();
    }

    [Theory]
    [InlineData("mayo_missing")]   // nothing matches
    [InlineData("statuscode")]     // entity-scoped name: existing not-found hint path must still see the server fault
    [Trait("AC", "AC-134")]
    public async Task Unresolvable_KeepsOriginalServerFault(string name)
    {
        var f = new Fixture();
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync(name));
        Assert.Contains("Could not find an optionset with name", ex.Message);
        Assert.Single(f.Requests.OfType<RetrieveOptionSetRequest>());
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task AmbiguousVariants_KeepOriginalServerFault()
    {
        var f = new Fixture { Catalog = [Choice("MAYO_X"), Choice("mayo_x")] };
        await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync("Mayo_X"));
        Assert.Single(f.Requests.OfType<RetrieveOptionSetRequest>());
    }

    [Theory]
    [InlineData("mayo_airequesttype")]
    [InlineData("mayo_AIRequestType")]
    [Trait("AC", "AC-134")]
    public async Task OtherServerFault_DoesNotList_OrRetry(string name)
    {
        var privilege = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040220), Message = "Principal user is missing prvReadOptionSet privilege." },
            "Principal user is missing prvReadOptionSet privilege.");
        var f = new Fixture { RetrieveFailure = privilege };
        Assert.Same(privilege, await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync(name)));
        Assert.Single(f.Requests);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        f.AssertLeases();
    }

    [Theory]
    [InlineData("Could not find solution with name SharedOptionSets")]
    [InlineData("Could not find an optionset with name mayo_other")]
    [InlineData("Could not find option value 5 in optionset mayo_AIRequestType")]
    [Trait("AC", "AC-134")]
    public async Task NearMissNotFoundFault_DoesNotList_OrRetry(string message)
    {
        // The text fallback only applies to faults that do not carry the ObjectDoesNotExist code.
        var f = new Fixture { NotFoundMessage = _ => message, NotFoundCode = 0x80040203 };
        await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync("mayo_AIRequestType"));
        Assert.Single(f.Requests);
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-133")]
    public async Task NonEnglishNotFoundFault_ResolvesByFaultCode()
    {
        var f = new Fixture { NotFoundMessage = name => $"Impossible de trouver un groupe d'options nommé {name}" };
        var result = await f.Service.GetOptionSetAsync("mayo_AIRequestType");
        Assert.Equal(Stored, result.Name);
        Assert.Single(f.Requests.OfType<RetrieveAllOptionSetsRequest>());
        Assert.Equal(new[] { "mayo_AIRequestType", Stored }, f.Requests.OfType<RetrieveOptionSetRequest>().Select(r => r.Name));
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-133")]
    public async Task EnglishWordingWithAnotherCode_StillResolves()
    {
        var f = new Fixture { NotFoundCode = 0x80040203 };
        var result = await f.Service.GetOptionSetAsync("mayo_AIRequestType");
        Assert.Equal(Stored, result.Name);
        Assert.Single(f.Requests.OfType<RetrieveAllOptionSetsRequest>());
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task NonEnglishFaultWithAnotherCode_DoesNotList()
    {
        var f = new Fixture { NotFoundMessage = name => $"Impossible de trouver un groupe d'options nommé {name}", NotFoundCode = 0x80040203 };
        await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync("mayo_AIRequestType"));
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
    }

    [Fact]
    [Trait("AC", "AC-133")]
    public async Task UnnamedNotFoundWording_TriggersResolution()
    {
        // The wording quoted in #1535 echoes no name; it must still resolve the stored spelling.
        var f = new Fixture { NotFoundMessage = _ => "Could not find an option set with specified name" };
        var result = await f.Service.GetOptionSetAsync("mayo_AIRequestType");
        Assert.Equal(Stored, result.Name);
        Assert.Single(f.Requests.OfType<RetrieveAllOptionSetsRequest>());
        Assert.Equal(new[] { "mayo_AIRequestType", Stored }, f.Requests.OfType<RetrieveOptionSetRequest>().Select(r => r.Name));
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task CatalogLookupFailure_KeepsTheOriginalFault()
    {
        var f = new Fixture { CatalogFailure = _ => new TimeoutException("Synthetic catalog timeout") };
        var ex = await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync("mayo_AIRequestType"));
        Assert.Contains("Could not find an optionset with name mayo_AIRequestType", ex.Message);
        Assert.Single(f.Requests.OfType<RetrieveOptionSetRequest>());
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task NonCallerCancellationDuringListing_KeepsTheOriginalFault()
    {
        var f = new Fixture { CatalogFailure = _ => new TaskCanceledException("Synthetic HTTP-layer timeout") };
        await Assert.ThrowsAsync<FaultException<OrganizationServiceFault>>(() => f.Service.GetOptionSetAsync("mayo_AIRequestType"));
        Assert.Single(f.Requests.OfType<RetrieveOptionSetRequest>());
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task CallerCancellationDuringListing_Propagates()
    {
        var f = new Fixture();
        f.CatalogFailure = fixture => { fixture.Cancellation.Cancel(); return new OperationCanceledException(fixture.Cancellation.Token); };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.GetOptionSetAsync("mayo_AIRequestType", f.Cancellation.Token));
        Assert.Equal(f.Cancellation.Token, ex.CancellationToken);
        f.AssertLeases();
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task NonFaultFailure_DoesNotList()
    {
        var timeout = new TimeoutException("Synthetic timeout");
        var f = new Fixture { RetrieveFailure = timeout };
        Assert.Same(timeout, await Assert.ThrowsAsync<TimeoutException>(() => f.Service.GetOptionSetAsync("mayo_AIRequestType")));
        Assert.DoesNotContain(f.Requests, r => r is RetrieveAllOptionSetsRequest);
    }

    private static OptionSetMetadata Choice(string name)
    {
        var choice = new OptionSetMetadata { Name = name, IsGlobal = true, OptionSetType = OptionSetType.Picklist };
        choice.Options.Add(new OptionMetadata(new Label("One", 1033), 1));
        return choice;
    }

    private sealed class Fixture
    {
        private int _active, _acquired, _released, _peak;
        internal List<OrganizationRequest> Requests { get; } = [];
        internal OptionSetMetadataBase[] Catalog { get; init; } = [Choice(Stored)];
        internal Exception? RetrieveFailure { get; init; }
        internal Func<Fixture, Exception>? CatalogFailure { get; set; }
        internal Func<string, string>? NotFoundMessage { get; init; }
        internal uint NotFoundCode { get; init; } = 0x80040217; // ObjectDoesNotExist
        internal CancellationTokenSource Cancellation { get; } = new();
        internal DataverseMetadataQueryService Service { get; }

        internal Fixture()
        {
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                _active++; _acquired++; _peak = Math.Max(_peak, _active);
                var client = new Mock<IPooledClient>(MockBehavior.Strict);
                client.Setup(c => c.DisposeAsync()).Returns(() => { _active--; _released++; return ValueTask.CompletedTask; });
                client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                    .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                    {
                        Requests.Add(request);
                        return Task.FromResult(Execute(request));
                    });
                return client.Object;
            });
            Service = new DataverseMetadataQueryService(pool.Object);
        }

        private OrganizationResponse Execute(OrganizationRequest request)
        {
            switch (request)
            {
                case RetrieveAllOptionSetsRequest:
                    if (CatalogFailure != null) throw CatalogFailure(this);
                    return new RetrieveAllOptionSetsResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = Catalog } };
                case RetrieveOptionSetRequest retrieve:
                    if (RetrieveFailure != null) throw RetrieveFailure;
                    var match = Catalog.FirstOrDefault(c => string.Equals(c.Name, retrieve.Name, StringComparison.Ordinal));
                    if (match == null)
                    {
                        // Recorded server wording (docs/qa/shakedown-v1-2026-04-20.md), unless a test overrides it.
                        var message = NotFoundMessage?.Invoke(retrieve.Name) ?? $"Could not find an optionset with name {retrieve.Name}";
                        throw new FaultException<OrganizationServiceFault>(
                            new OrganizationServiceFault { ErrorCode = unchecked((int)NotFoundCode), Message = message }, message);
                    }
                    return new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = match } };
                default:
                    throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName);
            }
        }

        internal void AssertLeases()
        {
            Assert.Equal(0, _active);
            Assert.Equal(_acquired, _released);
            Assert.Equal(1, _peak);
        }
    }
}

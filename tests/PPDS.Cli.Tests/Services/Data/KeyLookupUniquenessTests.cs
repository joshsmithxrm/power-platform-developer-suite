using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// <c>data delete --key</c> and <c>data update --key</c> act on a record only when exactly one record matches the key;
/// when more match, the lookup refuses before anything is written and says whether the columns are an alternate key
/// (#1918).
/// </summary>
[Trait("Category", "Unit")]
public class KeyLookupUniquenessTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly List<QueryExpression> _queries = [];
    private readonly List<OrganizationRequest> _requests = [];
    private Guid[] _matches = [];
    private EntityKeyMetadata[]? _keys = [];
    private Exception? _metadataFailure;

    public KeyLookupUniquenessTests()
    {
        _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Returns<QueryBase, CancellationToken>((q, _) =>
            {
                var query = (QueryExpression)q;
                _queries.Add(query);
                var take = query.TopCount ?? int.MaxValue;
                return Task.FromResult(new EntityCollection(
                    _matches.Take(take).Select(id => new Entity("account", id)).ToList()));
            });
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((r, _) =>
            {
                _requests.Add(r);
                if (_metadataFailure != null) throw _metadataFailure;
                var metadata = new EntityMetadata { LogicalName = "account" };
                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.Keys))!.SetValue(metadata, _keys);
                return Task.FromResult<OrganizationResponse>(new RetrieveEntityResponse
                {
                    Results = new ParameterCollection { ["EntityMetadata"] = metadata }
                });
            });
    }

    // DataQueryService's constructor is the one place to change if its dependencies change.
    private DataQueryService CreateService() => new(_pool.Object, FilterTestColumns.Provider(), NullLogger<DataQueryService>.Instance);

    private static EntityKeyMetadata Key(string name, EntityKeyIndexStatus status, params string[] columns)
    {
        var key = new EntityKeyMetadata { LogicalName = name, KeyAttributes = columns };
        typeof(EntityKeyMetadata).GetProperty(nameof(EntityKeyMetadata.EntityKeyIndexStatus))!.SetValue(key, status);
        return key;
    }

    [Fact]
    public async Task ThreeRecordsMatch_RefusedWithAtLeastTwo_AndNothingResolved()
    {
        _matches = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

        var act = () => CreateService().ResolveByAlternateKeyAsync("account", "name=Contoso");

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.KeyMatchesMultipleRecords);
        ex.UserMessage.Should().Contain("at least 2")
            .And.Contain("not an alternate key")
            .And.Contain("not guaranteed to be unique")
            .And.Contain("Nothing was changed");
        ex.UserMessage.Should().NotContain("Contoso", "the message names columns, never the key's values");
        _matches.Should().NotContain(m => ex.UserMessage.Contains(m.ToString()));
        _queries.Should().ContainSingle().Which.TopCount.Should().Be(2);
    }

    [Fact]
    public async Task DuplicatesUnderADefinedKeyWhoseIndexIsNotActive_MessageNamesTheKey()
    {
        _matches = [Guid.NewGuid(), Guid.NewGuid()];
        _keys = [Key("new_accountnumberkey", EntityKeyIndexStatus.Pending, "accountnumber", "new_region")];

        // Columns in another order and case still name the same key.
        var act = () => CreateService().ResolveByAlternateKeyAsync("account", "New_Region=West, accountnumber=A-1");

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.KeyMatchesMultipleRecords);
        ex.UserMessage.Should().Contain("alternate key 'new_accountnumberkey'")
            .And.Contain("not active (Pending)");
        ex.UserMessage.Should().NotContain("A-1").And.NotContain("West");
    }

    [Fact]
    public async Task ColumnsThatAreOnlyPartOfAKey_AreNotThatKey()
    {
        _matches = [Guid.NewGuid(), Guid.NewGuid()];
        _keys = [Key("new_compositekey", EntityKeyIndexStatus.Active, "accountnumber", "new_region")];

        var act = () => CreateService().ResolveByAlternateKeyAsync("account", "accountnumber=A-1");

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.UserMessage.Should().Contain("not an alternate key");
    }

    [Fact]
    public async Task KeysCannotBeRead_StillRefused()
    {
        _matches = [Guid.NewGuid(), Guid.NewGuid()];
        _metadataFailure = new InvalidOperationException("metadata unavailable");

        var act = () => CreateService().ResolveByAlternateKeyAsync("account", "name=Contoso");

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.KeyMatchesMultipleRecords);
        ex.UserMessage.Should().Contain("could not be read");
    }

    [Fact]
    public async Task ExactlyOneRecordMatches_ReturnsIt_WithoutReadingMetadata()
    {
        var only = Guid.NewGuid();
        _matches = [only];

        var resolved = await CreateService().ResolveByAlternateKeyAsync("account", "accountnumber=A-1");

        resolved.Should().Be(only);
        _requests.Should().BeEmpty();
        _queries.Should().ContainSingle().Which.TopCount.Should().Be(2);
    }

    [Fact]
    public async Task NoRecordMatches_ReturnsNull()
    {
        _matches = [];

        var resolved = await CreateService().ResolveByAlternateKeyAsync("account", "accountnumber=A-1");

        resolved.Should().BeNull();
    }
}

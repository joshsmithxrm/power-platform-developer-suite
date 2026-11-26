using Microsoft.Xrm.Sdk;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.StackProbe;

// In-memory stand-ins: nothing in the probe reaches Dataverse.

internal sealed class NoShakedown : IShakedownGuard
{
    public void EnsureCanMutate(string operationDescription) { }
}

internal sealed class EmptyQueryExecutor : IQueryExecutor
{
    private static QueryResult Empty() => new()
    {
        EntityLogicalName = "account",
        Columns =
        [
            new QueryColumn { LogicalName = "accountid" },
            new QueryColumn { LogicalName = "name" },
            new QueryColumn { LogicalName = "description" }
        ],
        Records = Array.Empty<IReadOnlyDictionary<string, QueryValue>>(),
        Count = 0
    };

    public Task<QueryResult> ExecuteFetchXmlAsync(string fetchXml, int? pageNumber = null, string? pagingCookie = null, bool includeCount = false, CancellationToken cancellationToken = default)
        => Task.FromResult(Empty());

    public Task<QueryResult> ExecuteFetchXmlAsync(string fetchXml, int? pageNumber, string? pagingCookie, bool includeCount, QueryExecutionOptions? executionOptions, CancellationToken cancellationToken = default)
        => Task.FromResult(Empty());

    public Task<QueryResult> ExecuteFetchXmlAllPagesAsync(string fetchXml, int maxRecords = 5000, CancellationToken cancellationToken = default)
        => Task.FromResult(Empty());

    public Task<long?> GetTotalRecordCountAsync(string entityLogicalName, CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(0);

    public Task<(DateTime? Min, DateTime? Max)> GetMinMaxCreatedOnAsync(string entityLogicalName, CancellationToken cancellationToken = default)
        => Task.FromResult<(DateTime?, DateTime?)>((null, null));
}

internal sealed class CountingBulkExecutor : IBulkOperationExecutor
{
    private static Task<BulkOperationResult> Done(int count) =>
        Task.FromResult(new BulkOperationResult { SuccessCount = count });

    public Task<BulkOperationResult> CreateMultipleAsync(string entityLogicalName, IEnumerable<Entity> entities, BulkOperationOptions? options = null, DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
        => Done(entities.Count());

    public Task<BulkOperationResult> UpdateMultipleAsync(string entityLogicalName, IEnumerable<Entity> entities, BulkOperationOptions? options = null, DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
        => Done(entities.Count());

    public Task<BulkOperationResult> UpsertMultipleAsync(string entityLogicalName, IEnumerable<Entity> entities, BulkOperationOptions? options = null, DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
        => Done(entities.Count());

    public Task<BulkOperationResult> DeleteMultipleAsync(string entityLogicalName, IEnumerable<Guid> ids, BulkOperationOptions? options = null, DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
        => Done(ids.Count());
}

internal sealed class AccountMetadata : ICachedMetadataProvider
{
    private static AttributeMetadataDto Attribute(string name, string type, bool primary = false) => new()
    {
        LogicalName = name,
        DisplayName = name,
        SchemaName = name,
        AttributeType = type,
        IsPrimaryId = primary,
        IsValidForCreate = !primary,
        IsValidForUpdate = !primary,
        IsValidForRead = true
    };

    public Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EntitySummary>>(
            [new EntitySummary { LogicalName = "account", DisplayName = "account", SchemaName = "account" }]);

    public Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(string entityLogicalName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AttributeMetadataDto>>(
        [
            Attribute("accountid", "Uniqueidentifier", primary: true),
            Attribute("name", "String"),
            Attribute("description", "String"),
            Attribute("numberofemployees", "Integer"),
            Attribute("createdon", "DateTime")
        ]);

    public Task<EntityRelationshipsDto> GetRelationshipsAsync(string entityLogicalName, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void InvalidateAll() { }
    public void InvalidateEntity(string entityLogicalName) { }
    public void InvalidateEntityList() { }
    public void InvalidateGlobalOptionSets() { }
}

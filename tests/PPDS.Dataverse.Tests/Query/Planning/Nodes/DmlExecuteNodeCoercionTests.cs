using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Progress;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

/// <summary>
/// Regression tests for issue #787: <c>DmlExecuteNode</c> must coerce primitive SQL
/// literals into Dataverse SDK types (<see cref="EntityReference"/>, <see cref="OptionSetValue"/>,
/// <see cref="Money"/>) when metadata is available. Tests drive the node end-to-end with
/// in-memory metadata + bulk executor stubs.
/// </summary>
[Trait("Category", "TuiUnit")]
public class DmlExecuteNodeCoercionTests
{
    [Fact]
    public async Task InsertValues_CoercesLookupAndChoice_ToSdkTypes()
    {
        var clinicId = Guid.NewGuid();
        var bulk = new RecordingBulkExecutor();
        var metadata = new StubMetadataProvider(PetAttributes());
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        CompiledScalarExpression nameExpr = _ => "Sir Waggington";
        CompiledScalarExpression breedExpr = _ => "Corgi";
        CompiledScalarExpression speciesExpr = _ => 100000000;
        CompiledScalarExpression clinicExpr = _ => clinicId.ToString();

        var node = DmlExecuteNode.InsertValues(
            "hsl_pet",
            new[] { "hsl_name", "hsl_breed", "hsl_species", "hsl_clinic" },
            new IReadOnlyList<CompiledScalarExpression>[]
            {
                new[] { nameExpr, breedExpr, speciesExpr, clinicExpr }
            });

        await ConsumeAsync(node, context);

        var entity = Assert.Single(bulk.Created);
        Assert.Equal("Sir Waggington", entity["hsl_name"]);
        Assert.Equal("Corgi", entity["hsl_breed"]);

        var species = Assert.IsType<OptionSetValue>(entity["hsl_species"]);
        Assert.Equal(100000000, species.Value);

        var clinic = Assert.IsType<EntityReference>(entity["hsl_clinic"]);
        Assert.Equal("hsl_clinic", clinic.LogicalName);
        Assert.Equal(clinicId, clinic.Id);
    }

    [Fact]
    public async Task InsertValues_WithoutMetadataProvider_IsRefused_InsteadOfSendingRawValues()
    {
        var bulk = new RecordingBulkExecutor();
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk);

        CompiledScalarExpression speciesExpr = _ => 100000000;
        var node = DmlExecuteNode.InsertValues(
            "hsl_pet",
            new[] { "hsl_species" },
            new IReadOnlyList<CompiledScalarExpression>[]
            {
                new[] { speciesExpr }
            });

        // No provider: a raw int would reach a choice column, so nothing is sent at all.
        var ex = await Assert.ThrowsAsync<DmlSafetyRefusalException>(() => ConsumeAsync(node, context));

        Assert.Contains("column metadata", ex.Message);
        Assert.Empty(bulk.Created);
    }

    [Fact]
    public async Task InsertValues_ColumnMissingFromMetadata_IsRefused()
    {
        var bulk = new RecordingBulkExecutor();
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: new StubMetadataProvider(PetAttributes()));
        CompiledScalarExpression value = _ => "x";
        var node = DmlExecuteNode.InsertValues(
            "hsl_pet",
            new[] { "hsl_nosuchcolumn" },
            new IReadOnlyList<CompiledScalarExpression>[] { new[] { value } });

        var ex = await Assert.ThrowsAsync<DmlSafetyRefusalException>(() => ConsumeAsync(node, context));

        Assert.Contains("hsl_nosuchcolumn", ex.Message);
        Assert.Empty(bulk.Created);
    }

    [Fact]
    public async Task Update_CoercesSetClauseValue_ToOptionSetValue()
    {
        var petId = Guid.NewGuid();
        var bulk = new RecordingBulkExecutor();
        var metadata = new StubMetadataProvider(PetAttributes());
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        var source = new StaticPlanNode(new[]
        {
            new QueryRow(
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hsl_petid"] = QueryValue.Simple(petId)
                },
                "hsl_pet")
        });

        CompiledScalarExpression speciesExpr = _ => 100000000;
        var node = DmlExecuteNode.Update(
            "hsl_pet",
            source,
            new[] { new CompiledSetClause("hsl_species", speciesExpr) });

        await ConsumeAsync(node, context);

        var entity = Assert.Single(bulk.Updated);
        Assert.Equal(petId, entity.Id);
        var species = Assert.IsType<OptionSetValue>(entity["hsl_species"]);
        Assert.Equal(100000000, species.Value);
    }

    [Fact]
    public async Task InsertSelect_CoercesSourceColumnValues_ToSdkTypes()
    {
        var clinicId = Guid.NewGuid();
        var bulk = new RecordingBulkExecutor();
        var metadata = new StubMetadataProvider(PetAttributes());
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        // Source row has primitive values — INSERT ... SELECT should coerce via metadata.
        var source = new StaticPlanNode(new[]
        {
            new QueryRow(
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hsl_name"] = QueryValue.Simple("Fido"),
                    ["hsl_species"] = QueryValue.Simple(100000001),
                    ["hsl_clinic"] = QueryValue.Simple(clinicId.ToString())
                },
                "hsl_pet")
        });

        var node = DmlExecuteNode.InsertSelect(
            "hsl_pet",
            new[] { "hsl_name", "hsl_species", "hsl_clinic" },
            source);

        await ConsumeAsync(node, context);

        var entity = Assert.Single(bulk.Created);
        Assert.Equal("Fido", entity["hsl_name"]);
        Assert.Equal(100000001, Assert.IsType<OptionSetValue>(entity["hsl_species"]).Value);
        Assert.Equal(clinicId, Assert.IsType<EntityReference>(entity["hsl_clinic"]).Id);
    }

    [Fact]
    public async Task InsertValues_CoercesDateTimeString_ToDateTime()
    {
        var bulk = new RecordingBulkExecutor();
        var metadata = new StubMetadataProvider(PetAttributes());
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        CompiledScalarExpression nameExpr = _ => "Buddy";
        CompiledScalarExpression dateExpr = _ => "2026-05-20";

        var node = DmlExecuteNode.InsertValues(
            "hsl_pet",
            new[] { "hsl_name", "hsl_birthdate" },
            new IReadOnlyList<CompiledScalarExpression>[]
            {
                new[] { nameExpr, dateExpr }
            });

        await ConsumeAsync(node, context);

        var entity = Assert.Single(bulk.Created);
        Assert.Equal("Buddy", entity["hsl_name"]);
        var birthdate = Assert.IsType<DateTime>(entity["hsl_birthdate"]);
        Assert.Equal(new DateTime(2026, 5, 20), birthdate);
    }

    [Fact]
    public async Task InsertValues_WithFailures_IncludesFailureCountInResult()
    {
        var bulk = new ConfigurableBulkExecutor(successCount: 2, failureCount: 1);
        var metadata = new StubMetadataProvider(PetAttributes());
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        var rows = new IReadOnlyList<CompiledScalarExpression>[]
        {
            new CompiledScalarExpression[] { _ => "Pet1" },
            new CompiledScalarExpression[] { _ => "Pet2" },
            new CompiledScalarExpression[] { _ => "Pet3" },
        };

        var node = DmlExecuteNode.InsertValues(
            "hsl_pet",
            new[] { "hsl_name" },
            rows);

        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context, CancellationToken.None))
        {
            results.Add(row);
        }

        var resultRow = Assert.Single(results);
        Assert.True(resultRow.Values.TryGetValue("affected_rows", out var affected));
        Assert.Equal(2L, affected.Value);
        Assert.True(resultRow.Values.TryGetValue("failed_rows", out var failed));
        Assert.Equal(1L, failed.Value);
    }

    [Fact]
    public async Task InsertValues_AllRowsFail_RecordsErrorInStatistics()
    {
        var bulk = new ConfigurableBulkExecutor(0, 1, "The 'CreateMultiple' method does not support entities of type 'solution'.");
        var context = new QueryPlanContext(new StubQueryExecutor(), bulkOperationExecutor: bulk,
            metadataProvider: new StubMetadataProvider(new[] { new AttributeMetadataDto { LogicalName = "friendlyname", DisplayName = "friendlyname", SchemaName = "friendlyname", AttributeType = "String" } }));
        var node = DmlExecuteNode.InsertValues(
            "solution",
            new[] { "friendlyname" },
            new IReadOnlyList<CompiledScalarExpression>[] { new CompiledScalarExpression[] { _ => "x" } });

        await foreach (var _ in node.ExecuteAsync(context, CancellationToken.None)) { }

        Assert.Contains("does not support entities of type 'solution'", Assert.Single(context.Statistics.DmlOutcomes).ErrorMessage);
    }

    [Fact]
    public async Task InsertValues_NoFailures_LeavesErrorNull()
    {
        var bulk = new ConfigurableBulkExecutor(1, 0, "ignored");
        var context = new QueryPlanContext(new StubQueryExecutor(), bulkOperationExecutor: bulk,
            metadataProvider: new StubMetadataProvider(new[] { new AttributeMetadataDto { LogicalName = "name", DisplayName = "name", SchemaName = "name", AttributeType = "String" } }));
        var node = DmlExecuteNode.InsertValues(
            "account",
            new[] { "name" },
            new IReadOnlyList<CompiledScalarExpression>[] { new CompiledScalarExpression[] { _ => "x" } });

        await foreach (var _ in node.ExecuteAsync(context, CancellationToken.None)) { }

        Assert.Null(Assert.Single(context.Statistics.DmlOutcomes).ErrorMessage);
    }

    [Fact]
    public async Task Update_WithFailures_IncludesFailureCountInResult()
    {
        var petId = Guid.NewGuid();
        var bulk = new ConfigurableBulkExecutor(successCount: 0, failureCount: 1);
        var metadata = new StubMetadataProvider(PetAttributes());
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        var source = new StaticPlanNode(new[]
        {
            new QueryRow(
                new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hsl_petid"] = QueryValue.Simple(petId)
                },
                "hsl_pet")
        });

        CompiledScalarExpression nameExpr = _ => "Updated";
        var node = DmlExecuteNode.Update(
            "hsl_pet",
            source,
            new[] { new CompiledSetClause("hsl_name", nameExpr) });

        var results = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context, CancellationToken.None))
        {
            results.Add(row);
        }

        var resultRow = Assert.Single(results);
        Assert.True(resultRow.Values.TryGetValue("failed_rows", out var failed));
        Assert.Equal(1L, failed.Value);
    }

    // ═══════════════════════════════════════════════════════════════════

    private static async Task ConsumeAsync(IQueryPlanNode node, QueryPlanContext context)
    {
        await foreach (var _ in node.ExecuteAsync(context, CancellationToken.None))
        {
            // drain
        }
    }

    private static IReadOnlyList<AttributeMetadataDto> PetAttributes() => new[]
    {
        new AttributeMetadataDto
        {
            LogicalName = "hsl_petid",
            DisplayName = "Pet ID",
            SchemaName = "hsl_petid",
            AttributeType = "Uniqueidentifier",
            IsPrimaryId = true
        },
        new AttributeMetadataDto
        {
            LogicalName = "hsl_name",
            DisplayName = "Name",
            SchemaName = "hsl_name",
            AttributeType = "String"
        },
        new AttributeMetadataDto
        {
            LogicalName = "hsl_breed",
            DisplayName = "Breed",
            SchemaName = "hsl_breed",
            AttributeType = "String"
        },
        new AttributeMetadataDto
        {
            LogicalName = "hsl_species",
            DisplayName = "Species",
            SchemaName = "hsl_species",
            AttributeType = "Picklist"
        },
        new AttributeMetadataDto
        {
            LogicalName = "hsl_clinic",
            DisplayName = "Clinic",
            SchemaName = "hsl_clinic",
            AttributeType = "Lookup",
            Targets = new List<string> { "hsl_clinic" }
        },
        new AttributeMetadataDto
        {
            LogicalName = "hsl_birthdate",
            DisplayName = "Birth Date",
            SchemaName = "hsl_birthdate",
            AttributeType = "DateTime"
        }
    };

    private sealed class RecordingBulkExecutor : IBulkOperationExecutor
    {
        public List<Entity> Created { get; } = new();
        public List<Entity> Updated { get; } = new();
        public List<Guid> Deleted { get; } = new();

        public Task<BulkOperationResult> CreateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Created.AddRange(entities);
            return Task.FromResult(new BulkOperationResult { SuccessCount = Created.Count });
        }

        public Task<BulkOperationResult> UpdateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Updated.AddRange(entities);
            return Task.FromResult(new BulkOperationResult { SuccessCount = Updated.Count });
        }

        public Task<BulkOperationResult> UpsertMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Created.AddRange(entities);
            return Task.FromResult(new BulkOperationResult { SuccessCount = Created.Count });
        }

        public Task<BulkOperationResult> DeleteMultipleAsync(
            string entityLogicalName,
            IEnumerable<Guid> ids,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Deleted.AddRange(ids);
            return Task.FromResult(new BulkOperationResult { SuccessCount = Deleted.Count });
        }
    }

    private sealed class StubMetadataProvider : ICachedMetadataProvider
    {
        private readonly IReadOnlyList<AttributeMetadataDto> _attrs;

        public StubMetadataProvider(IReadOnlyList<AttributeMetadataDto> attrs)
        {
            _attrs = attrs;
        }

        public Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntitySummary>>(Array.Empty<EntitySummary>());

        public Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(string entityLogicalName, CancellationToken ct = default)
            => Task.FromResult(_attrs);

        public Task<EntityRelationshipsDto> GetRelationshipsAsync(string entityLogicalName, CancellationToken ct = default)
            => Task.FromResult(new EntityRelationshipsDto { EntityLogicalName = entityLogicalName });

        public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateAll() { }
        public void InvalidateEntity(string entityLogicalName) { }
        public void InvalidateEntityList() { }
        public void InvalidateGlobalOptionSets() { }
    }

    private sealed class StubQueryExecutor : IQueryExecutor
    {
        public Task<QueryResult> ExecuteFetchXmlAsync(
            string fetchXml,
            int? pageNumber = null,
            string? pagingCookie = null,
            bool includeCount = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(QueryResult.Empty(""));

        public Task<QueryResult> ExecuteFetchXmlAllPagesAsync(
            string fetchXml,
            int maxRecords = 5000,
            CancellationToken cancellationToken = default)
            => Task.FromResult(QueryResult.Empty(""));
    }

    private sealed class ConfigurableBulkExecutor : IBulkOperationExecutor
    {
        private readonly int _successCount;
        private readonly int _failureCount;
        private readonly string? _errorMessage;

        public ConfigurableBulkExecutor(int successCount, int failureCount, string? errorMessage = null)
        {
            _successCount = successCount;
            _failureCount = failureCount;
            _errorMessage = errorMessage;
        }

        private BulkOperationResult Result() => new()
        {
            SuccessCount = _successCount,
            FailureCount = _failureCount,
            Errors = _errorMessage == null
                ? Array.Empty<BulkOperationError>()
                : new[] { new BulkOperationError { Index = 0, Message = _errorMessage } }
        };

        public Task<BulkOperationResult> CreateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result());

        public Task<BulkOperationResult> UpdateMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result());

        public Task<BulkOperationResult> UpsertMultipleAsync(
            string entityLogicalName,
            IEnumerable<Entity> entities,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result());

        public Task<BulkOperationResult> DeleteMultipleAsync(
            string entityLogicalName,
            IEnumerable<Guid> ids,
            BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null,
            IProgress<ProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result());
    }

    private sealed class StaticPlanNode : IQueryPlanNode
    {
        private readonly IEnumerable<QueryRow> _rows;

        public StaticPlanNode(IEnumerable<QueryRow> rows) { _rows = rows; }

        public string Description => "Static";
        public long EstimatedRows => _rows.Count();
        public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

        public async IAsyncEnumerable<QueryRow> ExecuteAsync(
            QueryPlanContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _rows)
            {
                await Task.Yield();
                yield return row;
            }
        }
    }

    private static StaticPlanNode IdRows(string entity, int count) => new(
        Enumerable.Range(0, count).Select(_ => new QueryRow(
            new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
            {
                [entity + "id"] = QueryValue.Simple(Guid.NewGuid())
            },
            entity)).ToArray());

    private static async Task Drain(DmlExecuteNode node, QueryPlanContext context)
    {
        await foreach (var _ in node.ExecuteAsync(context, CancellationToken.None)) { }
    }

    [Fact]
    public async Task Update_AllFail_RecordsOutcomeWithOperationEntityCountsAndError()
    {
        var context = new QueryPlanContext(
            new StubQueryExecutor(), bulkOperationExecutor: new ConfigurableBulkExecutor(0, 2, "update boom"),
            metadataProvider: new StubMetadataProvider(PetAttributes()));
        CompiledScalarExpression expr = _ => "n";
        var node = DmlExecuteNode.Update("hsl_pet", IdRows("hsl_pet", 2), new[] { new CompiledSetClause("hsl_name", expr) });

        await Drain(node, context);

        var outcome = Assert.Single(context.Statistics.DmlOutcomes);
        Assert.Equal(new DmlStatementOutcome(DmlOperation.Update, "hsl_pet", 0, 2, "update boom"), outcome);
    }

    [Fact]
    public async Task Delete_AllFail_RecordsOutcomeWithError()
    {
        var context = new QueryPlanContext(
            new StubQueryExecutor(), bulkOperationExecutor: new ConfigurableBulkExecutor(0, 3, "delete boom"));
        var node = DmlExecuteNode.Delete("hsl_pet", IdRows("hsl_pet", 3));

        await Drain(node, context);

        var outcome = Assert.Single(context.Statistics.DmlOutcomes);
        Assert.Equal(DmlOperation.Delete, outcome.Operation);
        Assert.Equal(3, outcome.FailedRows);
        Assert.Equal("delete boom", outcome.ErrorMessage);
    }

    [Fact]
    public async Task InsertSelect_AllFail_RecordsOutcomeWithError()
    {
        var context = new QueryPlanContext(
            new StubQueryExecutor(), bulkOperationExecutor: new ConfigurableBulkExecutor(0, 2, "insert-select boom"),
            metadataProvider: new StubMetadataProvider(PetAttributes()));
        var source = new StaticPlanNode(new[]
        {
            new QueryRow(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase) { ["hsl_name"] = QueryValue.Simple("a") }, "hsl_pet"),
            new QueryRow(new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase) { ["hsl_name"] = QueryValue.Simple("b") }, "hsl_pet"),
        });
        var node = DmlExecuteNode.InsertSelect("hsl_pet", new[] { "hsl_name" }, source);

        await Drain(node, context);

        var outcome = Assert.Single(context.Statistics.DmlOutcomes);
        Assert.Equal(DmlOperation.Insert, outcome.Operation);
        Assert.Equal("insert-select boom", outcome.ErrorMessage);
    }

    [Fact]
    public async Task Failure_WithoutErrorDetail_GetsFallbackMessage()
    {
        var context = new QueryPlanContext(
            new StubQueryExecutor(), bulkOperationExecutor: new ConfigurableBulkExecutor(0, 1));
        var node = DmlExecuteNode.Delete("hsl_pet", IdRows("hsl_pet", 1));

        await Drain(node, context);

        Assert.Contains("without an error message", Assert.Single(context.Statistics.DmlOutcomes).ErrorMessage);
    }

    [Fact]
    public async Task TwoStatements_ShareOneContext_KeepSeparateOutcomesInOrder()
    {
        var context = new QueryPlanContext(
            new StubQueryExecutor(), bulkOperationExecutor: new ConfigurableBulkExecutor(0, 1, "first"));
        await Drain(DmlExecuteNode.Delete("a", IdRows("a", 1)), context);
        await Drain(DmlExecuteNode.Delete("b", IdRows("b", 1)), context);

        Assert.Equal(new[] { "a", "b" }, context.Statistics.DmlOutcomes.Select(o => o.EntityLogicalName));
    }

    // ---- B-M1: a cancelled bulk write is an interrupted statement, never a completed one ------------------

    private sealed class ShortResultBulkExecutor : IBulkOperationExecutor
    {
        private readonly Func<int, CancellationToken, BulkOperationResult> _result;

        public ShortResultBulkExecutor(Func<int, CancellationToken, BulkOperationResult> result) => _result = result;

        public Task<BulkOperationResult> CreateMultipleAsync(string entityLogicalName, IEnumerable<Entity> entities, BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_result(entities.Count(), cancellationToken));

        public Task<BulkOperationResult> UpdateMultipleAsync(string entityLogicalName, IEnumerable<Entity> entities, BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_result(entities.Count(), cancellationToken));

        public Task<BulkOperationResult> UpsertMultipleAsync(string entityLogicalName, IEnumerable<Entity> entities, BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_result(entities.Count(), cancellationToken));

        public Task<BulkOperationResult> DeleteMultipleAsync(string entityLogicalName, IEnumerable<Guid> ids, BulkOperationOptions? options = null,
            DataverseClientOptions? clientOptions = null, IProgress<ProgressSnapshot>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_result(ids.Count(), cancellationToken));
    }

    [Fact]
    public async Task SequentialBulkWrite_CancelledPartWay_IsReportedInterrupted_WithWhatWasWritten()
    {
        // The sequential path returns normally when cancelled: the batches not sent report nothing.
        using var cts = new CancellationTokenSource();
        var bulk = new ShortResultBulkExecutor((submitted, _) =>
        {
            cts.Cancel();
            return new BulkOperationResult { SuccessCount = 2 };
        });
        var context = new QueryPlanContext(new StubQueryExecutor(), bulkOperationExecutor: bulk);
        var node = DmlExecuteNode.Delete("hsl_pet", IdRows("hsl_pet", 5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context, cts.Token)) { }
        });

        var outcome = Assert.Single(context.Statistics.DmlOutcomes);
        Assert.True(outcome.Interrupted);
        Assert.Equal(2, outcome.AffectedRows);
    }

    [Fact]
    public async Task BulkWrite_ReportingFewerRowsThanSubmitted_IsAnInterruption_NotASuccess()
    {
        var bulk = new ShortResultBulkExecutor((submitted, _) => new BulkOperationResult { SuccessCount = submitted - 1 });
        var context = new QueryPlanContext(new StubQueryExecutor(), bulkOperationExecutor: bulk,
            metadataProvider: new StubMetadataProvider(PetAttributes()));
        CompiledScalarExpression expr = _ => "n";
        var node = DmlExecuteNode.Update("hsl_pet", IdRows("hsl_pet", 3), new[] { new CompiledSetClause("hsl_name", expr) });

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(() => Drain(node, context));

        Assert.Contains("2 of 3 rows", ex.Message);
        var outcome = Assert.Single(context.Statistics.DmlOutcomes);
        Assert.True(outcome.Interrupted);
        Assert.Equal(2, outcome.AffectedRows);
    }

    [Fact]
    public async Task ParallelBulkWrite_Cancelled_IsReportedInterrupted()
    {
        // The parallel path throws when cancelled; the statement is recorded as interrupted before it propagates.
        using var cts = new CancellationTokenSource();
        var bulk = new ShortResultBulkExecutor((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var context = new QueryPlanContext(new StubQueryExecutor(), bulkOperationExecutor: bulk);
        var node = DmlExecuteNode.Delete("hsl_pet", IdRows("hsl_pet", 4));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context, cts.Token)) { }
        });

        Assert.True(Assert.Single(context.Statistics.DmlOutcomes).Interrupted);
    }
}

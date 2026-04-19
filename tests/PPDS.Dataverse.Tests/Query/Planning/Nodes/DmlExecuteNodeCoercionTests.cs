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
    public async Task InsertValues_WithoutMetadataProvider_PassesRawValues()
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

        await ConsumeAsync(node, context);

        // No provider → no coercion (documents the degraded fallback)
        var entity = Assert.Single(bulk.Created);
        Assert.IsType<int>(entity["hsl_species"]);
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
}

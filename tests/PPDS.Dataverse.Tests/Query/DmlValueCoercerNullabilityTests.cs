using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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

namespace PPDS.Dataverse.Tests.Query;

/// <summary>
/// Nullability coverage for <see cref="DmlValueCoercer"/> and <see cref="DmlExecuteNode"/>
/// — the most common production failure mode in PR #827's DML type coercion. Verifies
/// behavior when SQL DML writes <c>NULL</c> literals (or <c>NULL</c> source-column values)
/// to lookup / choice / currency columns, both nullable (<see cref="AttributeMetadataDto.RequiredLevel"/>
/// = "None") and required ("ApplicationRequired" / "SystemRequired").
/// </summary>
/// <remarks>
/// The existing test suite exercises happy-path coercion (int → OptionSetValue, GUID
/// string → EntityReference, etc.) but never passes a null <c>value</c> to a typed
/// attribute. Null is by far the most common "real world" mistake — users writing
/// <c>INSERT INTO account (primarycontactid) VALUES (NULL)</c> or <c>UPDATE account
/// SET revenue = NULL</c>. These tests lock in whatever the current behavior is so
/// regressions surface immediately.
/// </remarks>
[Trait("Category", "TuiUnit")]
public class DmlValueCoercerNullabilityTests
{
    // --- helpers --------------------------------------------------------

    private static AttributeMetadataDto Attr(
        string type,
        string name = "col",
        string? requiredLevel = null,
        List<string>? targets = null,
        string? attributeTypeName = null)
        => new()
        {
            LogicalName = name,
            DisplayName = name,
            SchemaName = name,
            AttributeType = type,
            RequiredLevel = requiredLevel,
            Targets = targets,
            AttributeTypeName = attributeTypeName
        };

    // ═══════════════════════════════════════════════════════════════════
    // Direct coercer tests — null handling per attribute type × required level
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Case 1: Null assignment to a <b>required</b> lookup column.
    /// Verifies whatever behavior the current coercer has. The early-return branch
    /// <c>if (value is null || attribute is null) return value;</c> means null passes
    /// through unchanged regardless of RequiredLevel — this test locks that in and
    /// surfaces a <b>potential production bug</b> (see file-level "Bugs surfaced"
    /// note returned to caller). Dataverse will later reject the row for the
    /// required attribute, producing an opaque SDK error rather than the structured
    /// <c>QueryErrorCode.TypeMismatch</c> the rest of the coercer emits.
    /// </summary>
    [Fact]
    public void Coerce_NullValue_RequiredLookup_ReturnsNull_DocumentsCurrentBehavior()
    {
        var attr = Attr(
            "Lookup",
            "primarycontactid",
            requiredLevel: "ApplicationRequired",
            targets: new() { "contact" });

        var result = DmlValueCoercer.Coerce(null, attr);

        // Current behavior: null passes through. No EntityReference is synthesized.
        // No PpdsException / QueryExecutionException is raised.
        result.Should().BeNull(
            because: "the coercer short-circuits on null input regardless of RequiredLevel; " +
            "Dataverse SDK later rejects the null for a required lookup with an opaque error");
    }

    /// <summary>
    /// Case 2: Null assignment to a <b>required</b> choice (picklist) column.
    /// Same short-circuit: null passes through; no structured error.
    /// </summary>
    [Fact]
    public void Coerce_NullValue_RequiredPicklist_ReturnsNull_DocumentsCurrentBehavior()
    {
        var attr = Attr(
            "Picklist",
            "statuscode",
            requiredLevel: "SystemRequired");

        var result = DmlValueCoercer.Coerce(null, attr);

        result.Should().BeNull(
            because: "null short-circuits the coercer; required choice columns get a raw null");
    }

    /// <summary>
    /// Case 3: Null assignment to a <b>required</b> currency (Money) column.
    /// Same short-circuit: null passes through; no Money wrapper, no structured error.
    /// </summary>
    [Fact]
    public void Coerce_NullValue_RequiredMoney_ReturnsNull_DocumentsCurrentBehavior()
    {
        var attr = Attr(
            "Money",
            "revenue",
            requiredLevel: "ApplicationRequired");

        var result = DmlValueCoercer.Coerce(null, attr);

        result.Should().BeNull(
            because: "null short-circuits before Money wrapping; required currency columns get a raw null");
    }

    // ────────── Secondary cases: nullable column null = success ─────────

    /// <summary>Case 5: Null on a nullable lookup column — should succeed (null passes through).</summary>
    [Fact]
    public void Coerce_NullValue_NullableLookup_ReturnsNull()
    {
        var attr = Attr(
            "Lookup",
            "parentcustomerid",
            requiredLevel: "None",
            targets: new() { "account" });

        DmlValueCoercer.Coerce(null, attr).Should().BeNull();
    }

    /// <summary>Case 6: Null on a nullable choice column — should succeed (null passes through).</summary>
    [Fact]
    public void Coerce_NullValue_NullableChoice_ReturnsNull()
    {
        var attr = Attr(
            "Picklist",
            "industrycode",
            requiredLevel: "None");

        DmlValueCoercer.Coerce(null, attr).Should().BeNull();
    }

    /// <summary>Case 6b: Null on a nullable Money column — should succeed (null passes through).</summary>
    [Fact]
    public void Coerce_NullValue_NullableMoney_ReturnsNull()
    {
        var attr = Attr(
            "Money",
            "creditlimit",
            requiredLevel: "None");

        DmlValueCoercer.Coerce(null, attr).Should().BeNull();
    }

    // ────────── Negative discriminator: null vs. missing reference ─────────

    /// <summary>
    /// Null value and "missing reference" (bad GUID string) MUST be treated differently.
    /// The bad-GUID path raises <see cref="QueryExecutionException"/>; the null path
    /// returns null. This locks in the distinction so a future refactor that conflates
    /// them (e.g., "treat null as ''") is caught.
    /// </summary>
    [Fact]
    public void Coerce_NullVsBadGuid_TreatedDifferently()
    {
        var attr = Attr("Lookup", "hsl_clinic", targets: new() { "hsl_clinic" });

        // Null path: no throw, returns null.
        DmlValueCoercer.Coerce(null, attr).Should().BeNull();

        // Missing/invalid GUID path: structured error.
        var act = () => DmlValueCoercer.Coerce("not-a-guid", attr);
        act.Should()
            .Throw<QueryExecutionException>()
            .Where(ex => ex.ErrorCode == QueryErrorCode.TypeMismatch);
    }

    /// <summary>
    /// Case 7: Non-null GUID for a lookup whose attribute metadata has a non-existent /
    /// empty Targets list (i.e., "no entity to reference") MUST raise structured.
    /// This is the closest proxy for "nonexistent target entity" that the coercer
    /// can actually detect — it has no entity-existence check, only Targets metadata.
    /// </summary>
    [Fact]
    public void Coerce_Lookup_EmptyTargets_RaisesStructured()
    {
        var attr = Attr("Lookup", "orphan", targets: new List<string>());

        var act = () => DmlValueCoercer.Coerce(Guid.NewGuid(), attr);
        act.Should()
            .Throw<QueryExecutionException>()
            .Where(ex => ex.ErrorCode == QueryErrorCode.TypeMismatch &&
                         ex.Message.Contains("no target entity", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Case 4: Mixed null + non-null rows in a single bulk DML batch
    // Drives DmlExecuteNode end-to-end with a recording bulk executor.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// INSERT VALUES batch of two rows: row 1 has NULL for a required lookup, row 2
    /// has a valid GUID. Verifies both rows reach the bulk executor (the batch is
    /// not aborted client-side) and that the null row preserves null in
    /// <c>Entity["hsl_clinic"]</c> while the valid row is coerced to
    /// <see cref="EntityReference"/>. Documents current behavior — neither atomic
    /// abort nor row-level structured error is emitted today.
    /// </summary>
    [Fact]
    public async Task InsertValues_MixedNullAndValidLookupRows_BothReachBulkExecutor()
    {
        var validId = Guid.NewGuid();
        var bulk = new RecordingBulkExecutor();
        var metadata = new StubMetadataProvider(ClinicLookupSchema(requiredLevel: "ApplicationRequired"));
        var context = new QueryPlanContext(
            new StubQueryExecutor(),
            bulkOperationExecutor: bulk,
            metadataProvider: metadata);

        CompiledScalarExpression nullClinic = _ => null;
        CompiledScalarExpression validClinic = _ => validId.ToString();
        CompiledScalarExpression nameA = _ => "Row-A";
        CompiledScalarExpression nameB = _ => "Row-B";

        var node = DmlExecuteNode.InsertValues(
            "hsl_pet",
            new[] { "hsl_name", "hsl_clinic" },
            new IReadOnlyList<CompiledScalarExpression>[]
            {
                new[] { nameA, nullClinic },
                new[] { nameB, validClinic }
            });

        await ConsumeAsync(node, context);

        bulk.Created.Should().HaveCount(2, "both rows reach the bulk executor; no client-side abort");

        var nullRow = bulk.Created[0];
        nullRow["hsl_name"].Should().Be("Row-A");
        // Dataverse SDK Entity indexer returns null for unset values too, so distinguish
        // "explicitly set to null" via Attributes dictionary.
        nullRow.Attributes.Should().ContainKey("hsl_clinic");
        nullRow["hsl_clinic"].Should().BeNull(
            because: "null for a required lookup is passed through unchanged by the coercer; " +
            "a hypothetical nullability check would raise QueryErrorCode.TypeMismatch instead");

        var validRow = bulk.Created[1];
        validRow["hsl_name"].Should().Be("Row-B");
        validRow["hsl_clinic"].Should().BeOfType<EntityReference>()
            .Which.Id.Should().Be(validId);
    }

    /// <summary>
    /// UPDATE SET with a null-emitting expression for a required lookup: the null
    /// MUST reach <c>Entity["hsl_clinic"]</c> unchanged (no synthetic EntityReference,
    /// no structured error). Locks in current behavior for UPDATE parallel to the
    /// INSERT VALUES case.
    /// </summary>
    [Fact]
    public async Task Update_NullLookupSetClause_LeavesAttributeNull()
    {
        var petId = Guid.NewGuid();
        var bulk = new RecordingBulkExecutor();
        var metadata = new StubMetadataProvider(ClinicLookupSchema(requiredLevel: "SystemRequired"));
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

        CompiledScalarExpression nullLookup = _ => null;
        var node = DmlExecuteNode.Update(
            "hsl_pet",
            source,
            new[] { new CompiledSetClause("hsl_clinic", nullLookup) });

        await ConsumeAsync(node, context);

        var updated = bulk.Updated.Should().ContainSingle().Subject;
        updated.Id.Should().Be(petId);
        updated.Attributes.Should().ContainKey("hsl_clinic");
        updated["hsl_clinic"].Should().BeNull(
            because: "the coercer short-circuits on null input; SystemRequired level is not enforced client-side");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers / stubs — mirror those in DmlExecuteNodeCoercionTests so this
    // test class is self-contained and can be run / moved independently.
    // ═══════════════════════════════════════════════════════════════════

    private static async Task ConsumeAsync(IQueryPlanNode node, QueryPlanContext context)
    {
        await foreach (var _ in node.ExecuteAsync(context, CancellationToken.None))
        {
            // drain
        }
    }

    private static IReadOnlyList<AttributeMetadataDto> ClinicLookupSchema(string? requiredLevel) => new[]
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
            AttributeType = "String",
            RequiredLevel = "None"
        },
        new AttributeMetadataDto
        {
            LogicalName = "hsl_clinic",
            DisplayName = "Clinic",
            SchemaName = "hsl_clinic",
            AttributeType = "Lookup",
            RequiredLevel = requiredLevel,
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

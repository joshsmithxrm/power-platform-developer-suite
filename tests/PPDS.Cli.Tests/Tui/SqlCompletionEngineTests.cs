using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Sql.Intellisense;
using Xunit;

namespace PPDS.Cli.Tests.Tui;

/// <summary>
/// Unit tests for <see cref="SqlCompletionEngine"/>.
/// Uses an inline mock of <see cref="ICachedMetadataProvider"/> returning fake metadata.
/// </summary>
[Trait("Category", "TuiUnit")]
public class SqlCompletionEngineTests
{
    private readonly SqlCompletionEngine _engine;
    private readonly StubMetadataProvider _metadata;

    public SqlCompletionEngineTests()
    {
        _metadata = new StubMetadataProvider();
        _engine = new SqlCompletionEngine(_metadata);
    }

    #region Keyword Completions

    [Fact]
    public async Task GetCompletions_EmptyInput_ReturnsStatementKeywords()
    {
        var completions = await _engine.GetCompletionsAsync("", 0);

        Assert.NotEmpty(completions);
        Assert.All(completions, c => Assert.Equal(SqlCompletionKind.Keyword, c.Kind));
        Assert.Contains(completions, c => c.Label == "SELECT");
        Assert.Contains(completions, c => c.Label == "INSERT");
    }

    [Fact]
    public async Task GetCompletions_AfterFromEntity_ReturnsClauseKeywords()
    {
        var sql = "SELECT name FROM account ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.NotEmpty(completions);
        Assert.Contains(completions, c => c.Label == "WHERE");
        Assert.Contains(completions, c => c.Label == "ORDER BY");
        Assert.Contains(completions, c => c.Label == "JOIN");
    }

    [Fact]
    public async Task GetCompletions_AfterOrderByAttr_ReturnsAscDesc()
    {
        var sql = "SELECT name FROM account ORDER BY name ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.NotEmpty(completions);
        Assert.Contains(completions, c => c.Label == "ASC");
        Assert.Contains(completions, c => c.Label == "DESC");
    }

    [Fact]
    public async Task GetCompletions_KeywordsFilteredByPrefix()
    {
        // "SELECT name FROM account WH|" — should filter to keywords starting with WH
        var sql = "SELECT name FROM account WH";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        // "WH" prefix should match WHERE
        Assert.Contains(completions, c => c.Label == "WHERE");
        Assert.DoesNotContain(completions, c => c.Label == "JOIN");
    }

    #endregion

    #region Entity Completions

    [Fact]
    public async Task GetCompletions_AfterFrom_ReturnsEntityNames()
    {
        var sql = "SELECT name FROM ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.NotEmpty(completions);
        Assert.All(completions, c => Assert.Equal(SqlCompletionKind.Entity, c.Kind));
        Assert.Contains(completions, c => c.Label == "account");
        Assert.Contains(completions, c => c.Label == "contact");
    }

    [Fact]
    public async Task GetCompletions_AfterFromWithPrefix_FiltersEntities()
    {
        var sql = "SELECT name FROM acc";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.Contains(completions, c => c.Label == "account");
        Assert.DoesNotContain(completions, c => c.Label == "contact");
    }

    [Fact]
    public async Task GetCompletions_AfterJoin_ReturnsEntityNames()
    {
        var sql = "SELECT * FROM account a JOIN ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.NotEmpty(completions);
        Assert.Contains(completions, c => c.Label == "contact");
    }

    [Fact]
    public async Task GetCompletions_EntityDetails_IncludeDisplayNameAndCustomFlag()
    {
        var sql = "SELECT name FROM ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        var accountCompletion = completions.FirstOrDefault(c => c.Label == "account");
        Assert.NotNull(accountCompletion);
        Assert.Equal("Account", accountCompletion.Description);
        Assert.Equal("System", accountCompletion.Detail);

        var customCompletion = completions.FirstOrDefault(c => c.Label == "ppds_project");
        Assert.NotNull(customCompletion);
        Assert.Equal("Custom", customCompletion.Detail);
    }

    #endregion

    #region Attribute Completions

    [Fact]
    public async Task GetCompletions_AfterWhere_ReturnsAttributes()
    {
        var sql = "SELECT name FROM account WHERE ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.NotEmpty(completions);
        Assert.Contains(completions, c => c.Label == "name" && c.Kind == SqlCompletionKind.Attribute);
        Assert.Contains(completions, c => c.Label == "accountid" && c.Kind == SqlCompletionKind.Attribute);
    }

    [Fact]
    public async Task GetCompletions_AfterAliasDot_ReturnsAttributesForSpecificEntity()
    {
        // Alias "a" resolves to "account"
        var sql = "SELECT a. FROM account a";
        var cursorPos = "SELECT a.".Length;
        var completions = await _engine.GetCompletionsAsync(sql, cursorPos);

        Assert.NotEmpty(completions);
        Assert.Contains(completions, c => c.Label == "accountid");
        Assert.Contains(completions, c => c.Label == "name");
    }

    [Fact]
    public async Task GetCompletions_AfterAliasDotWithPrefix_FiltersAttributes()
    {
        var sql = "SELECT a.na FROM account a";
        var cursorPos = "SELECT a.na".Length;
        var completions = await _engine.GetCompletionsAsync(sql, cursorPos);

        Assert.Contains(completions, c => c.Label == "name");
        Assert.DoesNotContain(completions, c => c.Label == "accountid");
    }

    [Fact]
    public async Task GetCompletions_AfterOrderBy_ReturnsAttributes()
    {
        var sql = "SELECT name FROM account ORDER BY ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        Assert.NotEmpty(completions);
        Assert.Contains(completions, c => c.Kind == SqlCompletionKind.Attribute);
    }

    [Fact]
    public async Task GetCompletions_AttributeDetails_IncludeTypeAndDisplayName()
    {
        var sql = "SELECT name FROM account WHERE ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        var nameAttr = completions.FirstOrDefault(c => c.Label == "name");
        Assert.NotNull(nameAttr);
        Assert.Equal("Account Name", nameAttr.Description);
        Assert.Equal("String", nameAttr.Detail);
    }

    #endregion

    #region Sort Order

    [Fact]
    public async Task GetCompletions_CompletionsAreSorted()
    {
        var sql = "SELECT name FROM ";
        var completions = await _engine.GetCompletionsAsync(sql, sql.Length);

        // Verify sorted by SortOrder then alphabetically
        for (var i = 1; i < completions.Count; i++)
        {
            var prev = completions[i - 1];
            var curr = completions[i];

            if (prev.SortOrder == curr.SortOrder)
            {
                Assert.True(
                    string.Compare(prev.Label, curr.Label, StringComparison.OrdinalIgnoreCase) <= 0,
                    $"Expected '{prev.Label}' before '{curr.Label}' (alphabetical within same sort order)");
            }
            else
            {
                Assert.True(prev.SortOrder <= curr.SortOrder,
                    $"Expected sort order {prev.SortOrder} <= {curr.SortOrder}");
            }
        }
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task GetCompletions_CancelledToken_ThrowsOrReturnsEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should throw OperationCanceledException or return gracefully
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _engine.GetCompletionsAsync("SELECT name FROM ", 17, cts.Token));
    }

    #endregion

    #region Stub Metadata Provider

    /// <summary>
    /// Inline stub implementing <see cref="ICachedMetadataProvider"/> with fake Dataverse metadata.
    /// </summary>
    private sealed class StubMetadataProvider : ICachedMetadataProvider
    {
        private static readonly IReadOnlyList<EntitySummary> Entities = new List<EntitySummary>
        {
            new()
            {
                LogicalName = "account",
                DisplayName = "Account",
                SchemaName = "Account",
                IsCustomEntity = false,
                ObjectTypeCode = 1
            },
            new()
            {
                LogicalName = "contact",
                DisplayName = "Contact",
                SchemaName = "Contact",
                IsCustomEntity = false,
                ObjectTypeCode = 2
            },
            new()
            {
                LogicalName = "ppds_project",
                DisplayName = "Project",
                SchemaName = "ppds_Project",
                IsCustomEntity = true,
                ObjectTypeCode = 10001
            }
        };

        private static readonly Dictionary<string, IReadOnlyList<AttributeMetadataDto>> AttributesByEntity = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new List<AttributeMetadataDto>
            {
                new()
                {
                    LogicalName = "accountid",
                    DisplayName = "Account ID",
                    SchemaName = "AccountId",
                    AttributeType = "Uniqueidentifier",
                    IsPrimaryId = true
                },
                new()
                {
                    LogicalName = "name",
                    DisplayName = "Account Name",
                    SchemaName = "Name",
                    AttributeType = "String",
                    IsPrimaryName = true
                },
                new()
                {
                    LogicalName = "revenue",
                    DisplayName = "Annual Revenue",
                    SchemaName = "Revenue",
                    AttributeType = "Money"
                },
                new()
                {
                    LogicalName = "numberofemployees",
                    DisplayName = "Number of Employees",
                    SchemaName = "NumberOfEmployees",
                    AttributeType = "Integer"
                }
            },
            ["contact"] = new List<AttributeMetadataDto>
            {
                new()
                {
                    LogicalName = "contactid",
                    DisplayName = "Contact ID",
                    SchemaName = "ContactId",
                    AttributeType = "Uniqueidentifier",
                    IsPrimaryId = true
                },
                new()
                {
                    LogicalName = "fullname",
                    DisplayName = "Full Name",
                    SchemaName = "FullName",
                    AttributeType = "String",
                    IsPrimaryName = true
                },
                new()
                {
                    LogicalName = "parentcustomerid",
                    DisplayName = "Company Name",
                    SchemaName = "ParentCustomerId",
                    AttributeType = "Lookup"
                }
            },
            ["ppds_project"] = new List<AttributeMetadataDto>
            {
                new()
                {
                    LogicalName = "ppds_projectid",
                    DisplayName = "Project ID",
                    SchemaName = "ppds_ProjectId",
                    AttributeType = "Uniqueidentifier",
                    IsPrimaryId = true,
                    IsCustomAttribute = true
                },
                new()
                {
                    LogicalName = "ppds_name",
                    DisplayName = "Name",
                    SchemaName = "ppds_Name",
                    AttributeType = "String",
                    IsPrimaryName = true,
                    IsCustomAttribute = true
                }
            }
        };

        public Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Entities);
        }

        public Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(
            string entityLogicalName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (AttributesByEntity.TryGetValue(entityLogicalName, out var attrs))
            {
                return Task.FromResult(attrs);
            }
            return Task.FromResult<IReadOnlyList<AttributeMetadataDto>>(
                Array.Empty<AttributeMetadataDto>());
        }

        public Task<EntityRelationshipsDto> GetRelationshipsAsync(
            string entityLogicalName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new EntityRelationshipsDto
            {
                EntityLogicalName = entityLogicalName
            });
        }

        public Task PreloadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void InvalidateAll() { }

        public void InvalidateEntity(string entityLogicalName) { }
    }

    #endregion
}

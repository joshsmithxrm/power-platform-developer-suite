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
/// Unit tests for <see cref="SqlValidator"/>.
/// Uses an inline stub of <see cref="ICachedMetadataProvider"/> returning fake metadata.
/// </summary>
[Trait("Category", "TuiUnit")]
public class SqlValidatorTests
{
    private readonly SqlValidator _validator;
    private readonly SqlValidator _parseOnlyValidator;

    public SqlValidatorTests()
    {
        _validator = new SqlValidator(new StubMetadataProvider());
        _parseOnlyValidator = new SqlValidator(null);
    }

    #region Parse Error Detection

    [Fact]
    public async Task Validate_MalformedSql_ReturnsParseDiagnostic()
    {
        var diagnostics = await _validator.ValidateAsync("SELECT FROM");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Severity == SqlDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Validate_UnterminatedString_ReturnsParseDiagnostic()
    {
        var diagnostics = await _validator.ValidateAsync("SELECT name FROM account WHERE name = 'test");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Severity == SqlDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Validate_MissingFromClause_ReturnsParseDiagnostic()
    {
        var diagnostics = await _validator.ValidateAsync("SELECT name WHERE name = 'test'");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Severity == SqlDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Validate_ParseOnlyMode_ReportsParseErrors()
    {
        var diagnostics = await _parseOnlyValidator.ValidateAsync("SELECT FROM");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Severity == SqlDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Validate_ParseOnlyMode_NoMetadataErrors()
    {
        // Valid SQL with unknown entity — no metadata provider so no entity errors
        var diagnostics = await _parseOnlyValidator.ValidateAsync(
            "SELECT name FROM nonexistent_entity");

        Assert.Empty(diagnostics);
    }

    #endregion

    #region Unknown Entity Detection

    [Fact]
    public async Task Validate_UnknownEntity_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name FROM nonexistent_entity");

        Assert.NotEmpty(diagnostics);
        var entityDiag = diagnostics.First(d => d.Message.Contains("Unknown entity"));
        Assert.Equal(SqlDiagnosticSeverity.Warning, entityDiag.Severity);
        Assert.Contains("nonexistent_entity", entityDiag.Message);
    }

    [Fact]
    public async Task Validate_UnknownJoinEntity_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT a.name FROM account a JOIN unknown_table u ON a.accountid = u.accountid");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown entity") &&
            d.Message.Contains("unknown_table"));
    }

    [Fact]
    public async Task Validate_KnownEntity_NoEntityDiagnostic()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name FROM account");

        // Should have no entity-related diagnostics
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Unknown entity"));
    }

    [Fact]
    public async Task Validate_KnownEntity_CaseInsensitive()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name FROM Account");

        // Entity name matching should be case-insensitive
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Unknown entity"));
    }

    #endregion

    #region Unknown Attribute Detection

    [Fact]
    public async Task Validate_UnknownAttribute_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT nonexistent_column FROM account");

        Assert.NotEmpty(diagnostics);
        var attrDiag = diagnostics.First(d => d.Message.Contains("Unknown attribute"));
        Assert.Equal(SqlDiagnosticSeverity.Warning, attrDiag.Severity);
        Assert.Contains("nonexistent_column", attrDiag.Message);
        Assert.Contains("account", attrDiag.Message);
    }

    [Fact]
    public async Task Validate_UnknownAttributeInWhere_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name FROM account WHERE bad_column = 'test'");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown attribute") &&
            d.Message.Contains("bad_column"));
    }

    [Fact]
    public async Task Validate_KnownAttribute_NoAttributeDiagnostic()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name FROM account");

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Unknown attribute"));
    }

    [Fact]
    public async Task Validate_QualifiedAttribute_ResolvedToEntity()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT a.name FROM account a");

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Unknown attribute"));
    }

    [Fact]
    public async Task Validate_QualifiedUnknownAttribute_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT a.bad_attr FROM account a");

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown attribute") &&
            d.Message.Contains("bad_attr"));
    }

    #endregion

    #region Valid SQL Returns No Diagnostics

    [Fact]
    public async Task Validate_ValidSelectStar_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT * FROM account");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_ValidSelectWithWhere_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name, revenue FROM account WHERE name = 'test'");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_ValidJoin_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT a.name, c.fullname FROM account a JOIN contact c ON a.accountid = c.parentcustomerid");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_EmptyString_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync("");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_WhitespaceOnly_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync("   \n   ");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_ValidAggregate_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT COUNT(*) FROM account");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_ValidGroupBy_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name, COUNT(*) FROM account GROUP BY name");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Validate_ValidOrderBy_NoDiagnostics()
    {
        var diagnostics = await _validator.ValidateAsync(
            "SELECT name FROM account ORDER BY name ASC");

        Assert.Empty(diagnostics);
    }

    #endregion

    #region DML Validation

    [Fact]
    public async Task Validate_InsertUnknownEntity_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "INSERT INTO nonexistent (name) VALUES ('test')");

        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown entity"));
    }

    [Fact]
    public async Task Validate_InsertUnknownColumn_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "INSERT INTO account (bad_column) VALUES ('test')");

        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown attribute") &&
            d.Message.Contains("bad_column"));
    }

    [Fact]
    public async Task Validate_UpdateUnknownEntity_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "UPDATE nonexistent SET name = 'test' WHERE name = 'old'");

        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown entity"));
    }

    [Fact]
    public async Task Validate_DeleteUnknownEntity_ReturnsWarning()
    {
        var diagnostics = await _validator.ValidateAsync(
            "DELETE FROM nonexistent WHERE name = 'test'");

        Assert.Contains(diagnostics, d =>
            d.Severity == SqlDiagnosticSeverity.Warning &&
            d.Message.Contains("Unknown entity"));
    }

    #endregion

    #region Diagnostic Position

    [Fact]
    public async Task Validate_ParseError_HasCorrectPosition()
    {
        var diagnostics = await _validator.ValidateAsync("SELECT FROM");

        Assert.NotEmpty(diagnostics);
        var diag = diagnostics[0];
        Assert.True(diag.Start >= 0, "Diagnostic start should be non-negative");
        Assert.True(diag.Length > 0, "Diagnostic length should be positive");
    }

    [Fact]
    public async Task Validate_UnknownEntity_HasCorrectPosition()
    {
        var sql = "SELECT name FROM badentity";
        var diagnostics = await _validator.ValidateAsync(sql);

        var entityDiag = diagnostics.FirstOrDefault(d => d.Message.Contains("Unknown entity"));
        Assert.NotNull(entityDiag);
        Assert.Equal("badentity".Length, entityDiag.Length);
        Assert.True(entityDiag.Start >= 0, "Diagnostic start should be non-negative");
    }

    [Fact]
    public async Task ValidateAsync_RepeatedIdentifier_DiagnosticPointsToCorrectPosition()
    {
        var diagnostics = await _validator.ValidateAsync("SELECT name FROM badentity");
        var entityDiag = diagnostics.FirstOrDefault(d => d.Message.Contains("badentity"));
        Assert.NotNull(entityDiag);
        // "badentity" starts at position 17
        Assert.Equal(17, entityDiag!.Start);
    }

    #endregion

    #region WHERE Condition Column Validation

    [Fact]
    public async Task ValidateAsync_WhereWithUnknownColumn_ReturnsDiagnostic()
    {
        var validator = new SqlValidator(new StubMetadataProvider());
        var sql = "SELECT name FROM account WHERE unknowncol = 'x'";

        var diags = await validator.ValidateAsync(sql);

        Assert.Contains(diags, d => d.Message.Contains("unknowncol", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_MultipleWhereConditions_AllValidated()
    {
        var validator = new SqlValidator(new StubMetadataProvider());
        var sql = "SELECT name FROM account WHERE badcol1 = 'x' AND badcol2 = 'y'";

        var diags = await validator.ValidateAsync(sql);

        Assert.True(diags.Count >= 2, $"Expected at least 2 diagnostics for unknown columns, got {diags.Count}");
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

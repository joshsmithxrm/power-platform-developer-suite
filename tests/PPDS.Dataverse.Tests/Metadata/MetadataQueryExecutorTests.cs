using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata;

[Trait("Category", "PlanUnit")]
public class MetadataQueryExecutorTests
{
    #region IsMetadataTable

    [Theory]
    [InlineData("metadata.entity")]
    [InlineData("metadata.attribute")]
    [InlineData("metadata.relationship_1_n")]
    [InlineData("metadata.relationship_n_n")]
    [InlineData("metadata.optionset")]
    [InlineData("metadata.optionsetvalue")]
    public void IsMetadataTable_ReturnsTrue_ForAllKnownTables(string name)
    {
        var executor = new MetadataQueryExecutor();
        Assert.True(executor.IsMetadataTable(name));
    }

    [Theory]
    [InlineData("METADATA.ENTITY")]
    [InlineData("Metadata.Attribute")]
    [InlineData("metadata.OPTIONSET")]
    public void IsMetadataTable_IsCaseInsensitive(string name)
    {
        var executor = new MetadataQueryExecutor();
        Assert.True(executor.IsMetadataTable(name));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    [InlineData("entity")]
    [InlineData("metadata.unknown")]
    [InlineData("dbo.entity")]
    [InlineData("")]
    public void IsMetadataTable_ReturnsFalse_ForRegularTables(string name)
    {
        var executor = new MetadataQueryExecutor();
        Assert.False(executor.IsMetadataTable(name));
    }

    #endregion

    #region GetAvailableColumns

    [Fact]
    public void GetAvailableColumns_ReturnsColumns_ForEntityTable()
    {
        var executor = new MetadataQueryExecutor();
        var columns = executor.GetAvailableColumns("entity");

        Assert.Contains("logicalname", columns);
        Assert.Contains("displayname", columns);
        Assert.Contains("iscustomentity", columns);
        Assert.Contains("ownershiptype", columns);
        Assert.Contains("entitysetname", columns);
    }

    [Fact]
    public void GetAvailableColumns_ReturnsColumns_ForAttributeTable()
    {
        var executor = new MetadataQueryExecutor();
        var columns = executor.GetAvailableColumns("attribute");

        Assert.Contains("logicalname", columns);
        Assert.Contains("entitylogicalname", columns);
        Assert.Contains("attributetype", columns);
        Assert.Contains("maxlength", columns);
        Assert.Contains("precision", columns);
    }

    [Fact]
    public void GetAvailableColumns_AcceptsSchemaQualifiedName()
    {
        var executor = new MetadataQueryExecutor();
        var columns = executor.GetAvailableColumns("metadata.entity");

        Assert.Contains("logicalname", columns);
    }

    [Fact]
    public void GetAvailableColumns_Throws_ForUnknownTable()
    {
        var executor = new MetadataQueryExecutor();

        var ex = Assert.Throws<ArgumentException>(() => executor.GetAvailableColumns("unknown_table"));
        Assert.Contains("Unknown metadata table", ex.Message);
    }

    [Fact]
    public void GetAvailableColumns_ReturnsDistinctColumnsPerTable()
    {
        var executor = new MetadataQueryExecutor();
        var tableNames = new[] { "entity", "attribute", "relationship_1_n", "relationship_n_n", "optionset", "optionsetvalue" };

        foreach (var table in tableNames)
        {
            var columns = executor.GetAvailableColumns(table);
            Assert.True(columns.Count > 0, $"Table {table} should have at least one column");
            Assert.Equal(columns.Count, columns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    #endregion

    #region QueryMetadataAsync - null provider

    [Fact]
    public async Task QueryMetadataAsync_WithNullService_ReturnsEmpty()
    {
        var executor = new MetadataQueryExecutor(metadataService: null);

        var results = await executor.QueryMetadataAsync("entity");

        Assert.Empty(results);
    }

    [Fact]
    public async Task QueryMetadataAsync_WithNullService_ReturnsEmpty_ForAnyTable()
    {
        var executor = new MetadataQueryExecutor(metadataService: null);

        foreach (var table in new[] { "entity", "attribute", "relationship_1_n", "relationship_n_n", "optionset", "optionsetvalue" })
        {
            var results = await executor.QueryMetadataAsync(table);
            Assert.Empty(results);
        }
    }

    #endregion

    #region QueryMetadataAsync - entity table

    [Fact]
    public async Task QueryEntityMetadata_MapsEntitySummaryToRows()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    SchemaName = "Account",
                    EntitySetName = "accounts",
                    ObjectTypeCode = 1,
                    IsCustomEntity = false,
                    OwnershipType = "UserOwned",
                    Description = "An account"
                },
                new()
                {
                    LogicalName = "ppds_project",
                    DisplayName = "Project",
                    SchemaName = "ppds_project",
                    EntitySetName = "ppds_projects",
                    ObjectTypeCode = 10001,
                    IsCustomEntity = true,
                    OwnershipType = "UserOwned",
                    Description = "A custom project entity"
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("entity");

        Assert.Equal(2, results.Count);

        // Verify first row
        Assert.Equal("account", results[0]["logicalname"].Value);
        Assert.Equal("Account", results[0]["displayname"].Value);
        Assert.Equal("Account", results[0]["schemaname"].Value);
        Assert.Equal("accounts", results[0]["entitysetname"].Value);
        Assert.Equal(1, results[0]["objecttypecode"].Value);
        Assert.Equal(false, results[0]["iscustomentity"].Value);
        Assert.Equal("UserOwned", results[0]["ownershiptype"].Value);
        Assert.Equal("An account", results[0]["description"].Value);

        // Verify second row
        Assert.Equal("ppds_project", results[1]["logicalname"].Value);
        Assert.Equal(true, results[1]["iscustomentity"].Value);
    }

    [Fact]
    public async Task QueryEntityMetadata_WithRequestedColumns_ReturnsOnlyRequested()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    SchemaName = "Account",
                    IsCustomEntity = false
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var requestedColumns = new[] { "logicalname", "displayname" };
        var results = await executor.QueryMetadataAsync("entity", requestedColumns);

        Assert.Single(results);
        Assert.Equal(2, results[0].Count);
        Assert.True(results[0].ContainsKey("logicalname"));
        Assert.True(results[0].ContainsKey("displayname"));
        Assert.False(results[0].ContainsKey("schemaname"));
    }

    #endregion

    #region QueryMetadataAsync - attribute table

    [Fact]
    public async Task QueryAttributeMetadata_MapsAttributesToRows()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    SchemaName = "Account",
                    IsCustomEntity = false
                }
            });

        mockService.Setup(s => s.GetAttributesAsync("account", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AttributeMetadataDto>
            {
                new()
                {
                    LogicalName = "name",
                    DisplayName = "Account Name",
                    SchemaName = "Name",
                    AttributeType = "String",
                    RequiredLevel = "ApplicationRequired",
                    IsCustomAttribute = false,
                    MaxLength = 200
                },
                new()
                {
                    LogicalName = "revenue",
                    DisplayName = "Annual Revenue",
                    SchemaName = "Revenue",
                    AttributeType = "Money",
                    RequiredLevel = "None",
                    IsCustomAttribute = false,
                    Precision = 2
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("attribute");

        Assert.Equal(2, results.Count);

        Assert.Equal("name", results[0]["logicalname"].Value);
        Assert.Equal("account", results[0]["entitylogicalname"].Value);
        Assert.Equal("String", results[0]["attributetype"].Value);
        Assert.Equal(200, results[0]["maxlength"].Value);
        Assert.Equal(true, results[0]["isrequired"].Value); // ApplicationRequired -> true

        Assert.Equal("revenue", results[1]["logicalname"].Value);
        Assert.Equal("Money", results[1]["attributetype"].Value);
        Assert.Equal(2, results[1]["precision"].Value);
        Assert.Equal(false, results[1]["isrequired"].Value); // None -> false
    }

    #endregion

    #region QueryMetadataAsync - optionset table

    [Fact]
    public async Task QueryOptionSetMetadata_MapsOptionSetsToRows()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetGlobalOptionSetsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OptionSetSummary>
            {
                new()
                {
                    Name = "ppds_status",
                    DisplayName = "Status",
                    OptionSetType = "Picklist",
                    IsGlobal = true,
                    Description = "Global status option set"
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("optionset");

        Assert.Single(results);
        Assert.Equal("ppds_status", results[0]["name"].Value);
        Assert.Equal("Status", results[0]["displayname"].Value);
        Assert.Equal("Picklist", results[0]["optionsettype"].Value);
        Assert.Equal(true, results[0]["isglobal"].Value);
        Assert.Equal("Global status option set", results[0]["description"].Value);
    }

    #endregion

    #region QueryMetadataAsync - optionsetvalue table

    [Fact]
    public async Task QueryOptionSetValues_MapsOptionValuesToRows()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetGlobalOptionSetsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OptionSetSummary>
            {
                new()
                {
                    Name = "ppds_status",
                    DisplayName = "Status",
                    OptionSetType = "Picklist",
                    IsGlobal = true
                }
            });

        mockService.Setup(s => s.GetOptionSetAsync("ppds_status", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OptionSetMetadataDto
            {
                Name = "ppds_status",
                DisplayName = "Status",
                OptionSetType = "Picklist",
                IsGlobal = true,
                Options = new List<OptionValueDto>
                {
                    new() { Value = 100000000, Label = "Active", Description = "Active status" },
                    new() { Value = 100000001, Label = "Inactive", Description = "Inactive status" }
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("optionsetvalue");

        Assert.Equal(2, results.Count);
        Assert.Equal("ppds_status", results[0]["optionsetname"].Value);
        Assert.Equal(100000000, results[0]["value"].Value);
        Assert.Equal("Active", results[0]["label"].Value);
        Assert.Equal("Active status", results[0]["description"].Value);

        Assert.Equal("ppds_status", results[1]["optionsetname"].Value);
        Assert.Equal(100000001, results[1]["value"].Value);
        Assert.Equal("Inactive", results[1]["label"].Value);
    }

    #endregion

    #region QueryMetadataAsync - relationship tables

    [Fact]
    public async Task QueryOneToManyRelationships_MapsRelationshipsToRows()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    SchemaName = "Account",
                    IsCustomEntity = false
                }
            });

        mockService.Setup(s => s.GetRelationshipsAsync("account", "OneToMany", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityRelationshipsDto
            {
                EntityLogicalName = "account",
                OneToMany = new List<RelationshipMetadataDto>
                {
                    new()
                    {
                        SchemaName = "account_contacts",
                        RelationshipType = "OneToMany",
                        ReferencedEntity = "account",
                        ReferencedAttribute = "accountid",
                        ReferencingEntity = "contact",
                        ReferencingAttribute = "parentcustomerid",
                        IsCustomRelationship = false,
                        SecurityTypes = "Append"
                    }
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("relationship_1_n");

        Assert.Single(results);
        Assert.Equal("account_contacts", results[0]["schemaname"].Value);
        Assert.Equal("contact", results[0]["referencingentity"].Value);
        Assert.Equal("account", results[0]["referencedentity"].Value);
        Assert.Equal("parentcustomerid", results[0]["referencingattribute"].Value);
        Assert.Equal("accountid", results[0]["referencedattribute"].Value);
    }

    [Fact]
    public async Task QueryManyToManyRelationships_MapsRelationshipsToRows()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    SchemaName = "Account",
                    IsCustomEntity = false
                }
            });

        mockService.Setup(s => s.GetRelationshipsAsync("account", "ManyToMany", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityRelationshipsDto
            {
                EntityLogicalName = "account",
                ManyToMany = new List<ManyToManyRelationshipDto>
                {
                    new()
                    {
                        SchemaName = "accountleads_association",
                        Entity1LogicalName = "account",
                        Entity2LogicalName = "lead",
                        IntersectEntityName = "accountleads",
                        Entity1IntersectAttribute = "accountid",
                        Entity2IntersectAttribute = "leadid",
                        IsCustomRelationship = false
                    }
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("relationship_n_n");

        Assert.Single(results);
        Assert.Equal("accountleads_association", results[0]["schemaname"].Value);
        Assert.Equal("account", results[0]["entity1logicalname"].Value);
        Assert.Equal("lead", results[0]["entity2logicalname"].Value);
        Assert.Equal("accountleads", results[0]["intersectentityname"].Value);
    }

    #endregion

    #region QueryMetadataAsync - unknown table

    [Fact]
    public async Task QueryMetadataAsync_Throws_ForUnknownTable()
    {
        var mockService = new Mock<IMetadataService>();
        var executor = new MetadataQueryExecutor(mockService.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            executor.QueryMetadataAsync("unknown_table"));
    }

    #endregion

    #region QueryMetadataAsync - column filtering

    [Fact]
    public async Task QueryMetadataAsync_RequestedColumnsNotInSchema_ReturnsNull()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetGlobalOptionSetsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OptionSetSummary>
            {
                new()
                {
                    Name = "test_os",
                    DisplayName = "Test",
                    OptionSetType = "Picklist"
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("optionset", new[] { "name", "nonexistent_column" });

        Assert.Single(results);
        Assert.Equal("test_os", results[0]["name"].Value);
        Assert.Null(results[0]["nonexistent_column"].Value);
    }

    #endregion

    #region QueryMetadataAsync - cancellation

    [Fact]
    public async Task QueryMetadataAsync_RespectssCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var executor = new MetadataQueryExecutor(mockService.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            executor.QueryMetadataAsync("entity", cancellationToken: cts.Token));
    }

    #endregion

    #region QueryMetadataAsync - schema-qualified table names

    [Fact]
    public async Task QueryMetadataAsync_AcceptsSchemaQualifiedTableName()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "account",
                    DisplayName = "Account",
                    SchemaName = "Account",
                    IsCustomEntity = false
                }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("metadata.entity");

        Assert.Single(results);
        Assert.Equal("account", results[0]["logicalname"].Value);
    }

    #endregion

    #region QueryMetadataAsync - deduplication

    [Fact]
    public async Task QueryOneToManyRelationships_DeduplicatesBySchemaName()
    {
        var mockService = new Mock<IMetadataService>();
        mockService.Setup(s => s.GetEntitiesAsync(false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new() { LogicalName = "account", DisplayName = "Account", SchemaName = "Account", IsCustomEntity = false },
                new() { LogicalName = "contact", DisplayName = "Contact", SchemaName = "Contact", IsCustomEntity = false }
            });

        // Same relationship appears from both entities
        var sharedRelationship = new RelationshipMetadataDto
        {
            SchemaName = "account_contacts",
            RelationshipType = "OneToMany",
            ReferencedEntity = "account",
            ReferencedAttribute = "accountid",
            ReferencingEntity = "contact",
            ReferencingAttribute = "parentcustomerid"
        };

        mockService.Setup(s => s.GetRelationshipsAsync("account", "OneToMany", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityRelationshipsDto
            {
                EntityLogicalName = "account",
                OneToMany = new List<RelationshipMetadataDto> { sharedRelationship }
            });

        mockService.Setup(s => s.GetRelationshipsAsync("contact", "OneToMany", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityRelationshipsDto
            {
                EntityLogicalName = "contact",
                OneToMany = new List<RelationshipMetadataDto> { sharedRelationship }
            });

        var executor = new MetadataQueryExecutor(mockService.Object);
        var results = await executor.QueryMetadataAsync("relationship_1_n");

        // Should be deduplicated to 1 row
        Assert.Single(results);
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_WithNullService_DoesNotThrow()
    {
        var executor = new MetadataQueryExecutor(metadataService: null);
        Assert.NotNull(executor);
    }

    [Fact]
    public void Constructor_Parameterless_DoesNotThrow()
    {
        var executor = new MetadataQueryExecutor();
        Assert.NotNull(executor);
    }

    #endregion
}

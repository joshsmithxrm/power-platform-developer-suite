using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Transpilation;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Unit tests for <see cref="SqlQueryResultExpander"/>.
/// </summary>
public class SqlQueryResultExpanderTests
{
    #region Empty/No-Op Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithEmptyRecords_ReturnsOriginalResult()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>(),
            Count = 0
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Same(result, expanded);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithNoExpandableColumns_ReturnsOriginalResult()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "name" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Test Account")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Same(result, expanded);
    }

    #endregion

    #region Lookup Column Expansion Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithLookupColumn_ExpandsToTwoColumns()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Equal(2, expanded.Columns.Count);
        Assert.Equal("ownerid", expanded.Columns[0].LogicalName);
        Assert.Equal("owneridname", expanded.Columns[1].LogicalName);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithLookupColumn_BaseColumnShowsGuid()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Base column should have GUID as value and no FormattedValue
        Assert.Equal(ownerId, record["ownerid"].Value);
        Assert.Null(record["ownerid"].FormattedValue);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithLookupColumn_NameColumnShowsDisplayName()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Name column should have display name as value
        Assert.Equal("Josh Smith", record["owneridname"].Value);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithLookupColumn_BothColumnsHaveLookupMetadata()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Both columns should have lookup metadata for navigation
        Assert.Equal(ownerId, record["ownerid"].LookupEntityId);
        Assert.Equal("systemuser", record["ownerid"].LookupEntityType);
        Assert.Equal(ownerId, record["owneridname"].LookupEntityId);
        Assert.Equal("systemuser", record["owneridname"].LookupEntityType);
    }

    #endregion

    #region OptionSet Column Expansion Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithOptionSetColumn_ExpandsToTwoColumns()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "statuscode" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["statuscode"] = QueryValue.WithFormatting(1, "Active")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Equal(2, expanded.Columns.Count);
        Assert.Equal("statuscode", expanded.Columns[0].LogicalName);
        Assert.Equal("statuscodename", expanded.Columns[1].LogicalName);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithOptionSetColumn_BaseColumnShowsIntValue()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "statuscode" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["statuscode"] = QueryValue.WithFormatting(1, "Active")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Base column should have integer value
        Assert.Equal(1, record["statuscode"].Value);
        Assert.Null(record["statuscode"].FormattedValue);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithOptionSetColumn_NameColumnShowsLabel()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "statuscode" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["statuscode"] = QueryValue.WithFormatting(1, "Active")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Name column should have label as value
        Assert.Equal("Active", record["statuscodename"].Value);
    }

    #endregion

    #region Boolean Column Expansion Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithBooleanColumn_ExpandsToTwoColumns()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ismanaged" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ismanaged"] = QueryValue.WithFormatting(true, "Yes")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Equal(2, expanded.Columns.Count);
        Assert.Equal("ismanaged", expanded.Columns[0].LogicalName);
        Assert.Equal("ismanagedname", expanded.Columns[1].LogicalName);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithBooleanColumn_BaseColumnShowsBoolValue()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ismanaged" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ismanaged"] = QueryValue.WithFormatting(true, "Yes")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Base column should have boolean value
        Assert.Equal(true, record["ismanaged"].Value);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithBooleanColumn_NameColumnShowsYesNo()
    {
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ismanaged" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ismanaged"] = QueryValue.WithFormatting(true, "Yes")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Name column should have "Yes" as value
        Assert.Equal("Yes", record["ismanagedname"].Value);
    }

    #endregion

    #region No Duplicate Column Tests

    [Fact]
    public void ExpandFormattedValueColumns_WhenNameColumnAlreadyExists_DoesNotDuplicate()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "ownerid" },
                new() { LogicalName = "owneridname" } // Already exists
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith"),
                    ["owneridname"] = QueryValue.Simple("Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Should still have exactly 2 columns, not 3
        Assert.Equal(2, expanded.Columns.Count);
    }

    #endregion

    #region Mixed Column Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithMixedColumns_ExpandsCorrectly()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "name" },
                new() { LogicalName = "ownerid" },
                new() { LogicalName = "statuscode" }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Test Account"),
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith"),
                    ["statuscode"] = QueryValue.WithFormatting(1, "Active")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        // Should have 5 columns: name, ownerid, owneridname, statuscode, statuscodename
        Assert.Equal(5, expanded.Columns.Count);
        Assert.Equal("name", expanded.Columns[0].LogicalName);
        Assert.Equal("ownerid", expanded.Columns[1].LogicalName);
        Assert.Equal("owneridname", expanded.Columns[2].LogicalName);
        Assert.Equal("statuscode", expanded.Columns[3].LogicalName);
        Assert.Equal("statuscodename", expanded.Columns[4].LogicalName);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithMixedColumns_PreservesNonExpandableColumns()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn>
            {
                new() { LogicalName = "name" },
                new() { LogicalName = "ownerid" }
            },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Test Account"),
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);
        var record = expanded.Records[0];

        // Non-expandable column should be unchanged
        Assert.Equal("Test Account", record["name"].Value);
    }

    #endregion

    #region Null Value Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithNullLookupValue_HandlesGracefully()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                },
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Null // Null in second record
                }
            },
            Count = 2
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Equal(2, expanded.Columns.Count);
        Assert.Null(expanded.Records[1]["ownerid"].Value);
        Assert.Null(expanded.Records[1]["owneridname"].Value);
    }

    #endregion

    #region Metadata Preservation Tests

    [Fact]
    public void ExpandFormattedValueColumns_PreservesResultMetadata()
    {
        var ownerId = Guid.NewGuid();
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1,
            TotalCount = 100,
            MoreRecords = true,
            PagingCookie = "test-cookie",
            PageNumber = 2,
            ExecutionTimeMs = 500,
            ExecutedFetchXml = "<fetch><entity name='account'/></fetch>",
            IsAggregate = false
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result);

        Assert.Equal("account", expanded.EntityLogicalName);
        Assert.Equal(1, expanded.Count);
        Assert.Equal(100, expanded.TotalCount);
        Assert.True(expanded.MoreRecords);
        Assert.Equal("test-cookie", expanded.PagingCookie);
        Assert.Equal(2, expanded.PageNumber);
        Assert.Equal(500, expanded.ExecutionTimeMs);
        Assert.Equal("<fetch><entity name='account'/></fetch>", expanded.ExecutedFetchXml);
        Assert.False(expanded.IsAggregate);
    }

    #endregion

    #region Virtual Column Tests

    [Fact]
    public void ExpandFormattedValueColumns_WithVirtualColumnOnly_ShowsOnlyNameColumn()
    {
        var ownerId = Guid.NewGuid();
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["owneridname"] = new VirtualColumnInfo
            {
                BaseColumnName = "ownerid",
                BaseColumnExplicitlyQueried = false // User only queried owneridname
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);

        // Should only have the *name column, not the base column
        Assert.Single(expanded.Columns);
        Assert.Equal("owneridname", expanded.Columns[0].LogicalName);

        // Record should only have the *name value
        var record = expanded.Records[0];
        Assert.False(record.ContainsKey("ownerid"));
        Assert.True(record.ContainsKey("owneridname"));
        Assert.Equal("Josh Smith", record["owneridname"].Value);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithBothBaseAndVirtualColumn_ShowsBothColumns()
    {
        var ownerId = Guid.NewGuid();
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["owneridname"] = new VirtualColumnInfo
            {
                BaseColumnName = "ownerid",
                BaseColumnExplicitlyQueried = true // User queried both ownerid AND owneridname
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);

        // Should have both columns
        Assert.Equal(2, expanded.Columns.Count);
        Assert.Equal("ownerid", expanded.Columns[0].LogicalName);
        Assert.Equal("owneridname", expanded.Columns[1].LogicalName);

        // Record should have both values
        var record = expanded.Records[0];
        Assert.Equal(ownerId, record["ownerid"].Value);
        Assert.Equal("Josh Smith", record["owneridname"].Value);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithVirtualColumn_PreservesLookupMetadataOnNameColumn()
    {
        var ownerId = Guid.NewGuid();
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["owneridname"] = new VirtualColumnInfo
            {
                BaseColumnName = "ownerid",
                BaseColumnExplicitlyQueried = false
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);
        var record = expanded.Records[0];

        // The *name column should still have lookup metadata for navigation
        Assert.Equal(ownerId, record["owneridname"].LookupEntityId);
        Assert.Equal("systemuser", record["owneridname"].LookupEntityType);
    }

    [Fact]
    public void ExpandFormattedValueColumns_WithVirtualOptionSetColumn_ShowsOnlyNameColumn()
    {
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["statuscodename"] = new VirtualColumnInfo
            {
                BaseColumnName = "statuscode",
                BaseColumnExplicitlyQueried = false
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "statuscode" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["statuscode"] = QueryValue.WithFormatting(1, "Active")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);

        // Should only have the *name column
        Assert.Single(expanded.Columns);
        Assert.Equal("statuscodename", expanded.Columns[0].LogicalName);

        // Record should have the label
        var record = expanded.Records[0];
        Assert.Equal("Active", record["statuscodename"].Value);
    }

    [Fact]
    public void ExpandFormattedValueColumns_VirtualColumnDoesNotTriggerAutoExpand()
    {
        var ownerId = Guid.NewGuid();
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>
        {
            ["owneridname"] = new VirtualColumnInfo
            {
                BaseColumnName = "ownerid",
                BaseColumnExplicitlyQueried = true // Both queried
            }
        };

        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new List<QueryColumn> { new() { LogicalName = "ownerid" } },
            Records = new List<IReadOnlyDictionary<string, QueryValue>>
            {
                new Dictionary<string, QueryValue>
                {
                    ["ownerid"] = QueryValue.Lookup(ownerId, "systemuser", "Josh Smith")
                }
            },
            Count = 1
        };

        var expanded = SqlQueryResultExpander.ExpandFormattedValueColumns(result, virtualColumns);

        // Should have exactly 2 columns (base + virtual), not 3 (no additional auto-expand)
        Assert.Equal(2, expanded.Columns.Count);
    }

    #endregion
}

using PPDS.Dataverse.Metadata;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata;

[Trait("Category", "PlanUnit")]
public class MetadataSchemaTests
{
    [Theory]
    [InlineData("metadata.entity")]
    [InlineData("metadata.attribute")]
    [InlineData("metadata.relationship_1_n")]
    [InlineData("metadata.relationship_n_n")]
    [InlineData("metadata.optionset")]
    [InlineData("metadata.optionsetvalue")]
    [InlineData("metadata.relationship")]
    [InlineData("metadata.key")]
    public void IsMetadataTable_ReturnsTrue_ForKnownTables(string name)
    {
        Assert.True(MetadataTableDefinitions.IsMetadataTable(name));
    }

    [Theory]
    [InlineData("metadata.ENTITY")]
    [InlineData("METADATA.entity")]
    [InlineData("Metadata.Attribute")]
    public void IsMetadataTable_IsCaseInsensitive(string name)
    {
        Assert.True(MetadataTableDefinitions.IsMetadataTable(name));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    [InlineData("entity")]
    [InlineData("metadata.unknown")]
    [InlineData("")]
    [InlineData("dbo.entity")]
    public void IsMetadataTable_ReturnsFalse_ForNonMetadataTables(string name)
    {
        Assert.False(MetadataTableDefinitions.IsMetadataTable(name));
    }

    [Theory]
    [InlineData("metadata.entity", "entity")]
    [InlineData("metadata.attribute", "attribute")]
    [InlineData("metadata.relationship_1_n", "relationship_1_n")]
    [InlineData("METADATA.ENTITY", "ENTITY")]
    public void GetTableName_ExtractsTableFromSchemaQualifiedName(string input, string expected)
    {
        Assert.Equal(expected, MetadataTableDefinitions.GetTableName(input));
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("account")]
    public void GetTableName_ReturnsInput_WhenNotSchemaQualified(string input)
    {
        Assert.Equal(input, MetadataTableDefinitions.GetTableName(input));
    }

    [Fact]
    public void GetColumns_ReturnsColumns_ForKnownTable()
    {
        var columns = MetadataTableDefinitions.GetColumns("entity");

        Assert.NotNull(columns);
        Assert.Contains("logicalname", columns);
        Assert.Contains("displayname", columns);
        Assert.Contains("schemaname", columns);
    }

    [Fact]
    public void GetColumns_ReturnsColumns_ForSchemaQualifiedName()
    {
        var columns = MetadataTableDefinitions.GetColumns("metadata.attribute");

        Assert.NotNull(columns);
        Assert.Contains("logicalname", columns);
        Assert.Contains("entitylogicalname", columns);
        Assert.Contains("attributetype", columns);
    }

    [Fact]
    public void GetColumns_ReturnsNull_ForUnknownTable()
    {
        var columns = MetadataTableDefinitions.GetColumns("unknown");

        Assert.Null(columns);
    }

    [Fact]
    public void Tables_ContainsAllExpectedTables()
    {
        Assert.Equal(8, MetadataTableDefinitions.Tables.Count);
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("entity"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("attribute"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("relationship_1_n"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("relationship_n_n"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("optionset"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("optionsetvalue"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("relationship"));
        Assert.True(MetadataTableDefinitions.Tables.ContainsKey("key"));
    }

    [Fact]
    public void EntityTable_HasExpectedColumns()
    {
        var columns = MetadataTableDefinitions.Tables["entity"];

        Assert.Contains("logicalname", columns);
        Assert.Contains("displayname", columns);
        Assert.Contains("pluraldisplayname", columns);
        Assert.Contains("description", columns);
        Assert.Contains("schemaname", columns);
        Assert.Contains("objecttypecode", columns);
        Assert.Contains("iscustomentity", columns);
        Assert.Contains("isactivity", columns);
        Assert.Contains("ownershiptype", columns);
        Assert.Contains("isvalidforadvancedfind", columns);
        Assert.Contains("iscustomizable", columns);
        Assert.Contains("isintersect", columns);
        Assert.Contains("isvirtual", columns);
        Assert.Contains("hasnotes", columns);
        Assert.Contains("hasactivities", columns);
        Assert.Contains("changetracking", columns);
        Assert.Contains("entitysetname", columns);
    }

    [Fact]
    public void AttributeTable_HasExpectedColumns()
    {
        var columns = MetadataTableDefinitions.Tables["attribute"];

        Assert.Contains("logicalname", columns);
        Assert.Contains("entitylogicalname", columns);
        Assert.Contains("displayname", columns);
        Assert.Contains("attributetype", columns);
        Assert.Contains("maxlength", columns);
        Assert.Contains("precision", columns);
    }

    [Fact]
    public void RelationshipOneToManyTable_HasExpectedColumns()
    {
        var columns = MetadataTableDefinitions.Tables["relationship_1_n"];

        Assert.Contains("schemaname", columns);
        Assert.Contains("referencingentity", columns);
        Assert.Contains("referencedentity", columns);
        Assert.Contains("referencingattribute", columns);
        Assert.Contains("referencedattribute", columns);
    }

    [Fact]
    public void RelationshipManyToManyTable_HasExpectedColumns()
    {
        var columns = MetadataTableDefinitions.Tables["relationship_n_n"];

        Assert.Contains("schemaname", columns);
        Assert.Contains("entity1logicalname", columns);
        Assert.Contains("entity2logicalname", columns);
        Assert.Contains("intersectentityname", columns);
    }

    [Fact]
    public void OptionSetTable_HasExpectedColumns()
    {
        var columns = MetadataTableDefinitions.Tables["optionset"];

        Assert.Contains("name", columns);
        Assert.Contains("displayname", columns);
        Assert.Contains("isglobal", columns);
        Assert.Contains("optionsettype", columns);
    }

    [Fact]
    public void OptionSetValueTable_HasExpectedColumns()
    {
        var columns = MetadataTableDefinitions.Tables["optionsetvalue"];

        Assert.Contains("optionsetname", columns);
        Assert.Contains("value", columns);
        Assert.Contains("label", columns);
        Assert.Contains("description", columns);
    }
}

using FluentAssertions;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Models;

public class FieldSchemaTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var field = new FieldSchema();

        field.LogicalName.Should().BeEmpty();
        field.DisplayName.Should().BeEmpty();
        field.Type.Should().BeEmpty();
        field.LookupEntity.Should().BeNull();
        field.IsCustomField.Should().BeFalse();
        field.IsRequired.Should().BeFalse();
        field.IsPrimaryKey.Should().BeFalse();
        field.IsValidForCreate.Should().BeTrue();
        field.IsValidForUpdate.Should().BeTrue();
        field.MaxLength.Should().BeNull();
        field.Precision.Should().BeNull();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var field = new FieldSchema
        {
            LogicalName = "accountname",
            DisplayName = "Account Name",
            Type = "string",
            IsCustomField = false,
            IsRequired = true,
            MaxLength = 100
        };

        field.LogicalName.Should().Be("accountname");
        field.DisplayName.Should().Be("Account Name");
        field.Type.Should().Be("string");
        field.IsCustomField.Should().BeFalse();
        field.IsRequired.Should().BeTrue();
        field.MaxLength.Should().Be(100);
    }

    [Fact]
    public void IsLookup_ReturnsTrueForEntityReference()
    {
        var field = new FieldSchema { Type = "entityreference" };

        field.IsLookup.Should().BeTrue();
    }

    [Fact]
    public void IsLookup_ReturnsTrueForLookup()
    {
        var field = new FieldSchema { Type = "lookup" };

        field.IsLookup.Should().BeTrue();
    }

    [Fact]
    public void IsLookup_ReturnsTrueForCustomer()
    {
        var field = new FieldSchema { Type = "customer" };

        field.IsLookup.Should().BeTrue();
    }

    [Fact]
    public void IsLookup_ReturnsTrueForOwner()
    {
        var field = new FieldSchema { Type = "owner" };

        field.IsLookup.Should().BeTrue();
    }

    [Fact]
    public void IsLookup_ReturnsTrueForPartyList()
    {
        var field = new FieldSchema { Type = "partylist" };

        field.IsLookup.Should().BeTrue();
    }

    [Fact]
    public void IsLookup_IsCaseInsensitive()
    {
        var field = new FieldSchema { Type = "EntityReference" };

        field.IsLookup.Should().BeTrue();
    }

    [Fact]
    public void IsLookup_ReturnsFalseForNonLookupTypes()
    {
        var field = new FieldSchema { Type = "string" };

        field.IsLookup.Should().BeFalse();
    }

    [Fact]
    public void IsPolymorphicLookup_ReturnsTrueForCustomer()
    {
        var field = new FieldSchema { Type = "customer" };

        field.IsPolymorphicLookup.Should().BeTrue();
    }

    [Fact]
    public void IsPolymorphicLookup_ReturnsTrueForOwner()
    {
        var field = new FieldSchema { Type = "owner" };

        field.IsPolymorphicLookup.Should().BeTrue();
    }

    [Fact]
    public void IsPolymorphicLookup_IsCaseInsensitive()
    {
        var field = new FieldSchema { Type = "Customer" };

        field.IsPolymorphicLookup.Should().BeTrue();
    }

    [Fact]
    public void IsPolymorphicLookup_ReturnsFalseForRegularLookup()
    {
        var field = new FieldSchema { Type = "lookup" };

        field.IsPolymorphicLookup.Should().BeFalse();
    }

    [Fact]
    public void LookupEntity_CanBeSet()
    {
        var field = new FieldSchema
        {
            Type = "lookup",
            LookupEntity = "account"
        };

        field.LookupEntity.Should().Be("account");
    }

    [Fact]
    public void IsValidForCreate_DefaultsToTrue()
    {
        var field = new FieldSchema();

        field.IsValidForCreate.Should().BeTrue();
    }

    [Fact]
    public void IsValidForUpdate_DefaultsToTrue()
    {
        var field = new FieldSchema();

        field.IsValidForUpdate.Should().BeTrue();
    }

    [Fact]
    public void ToString_ReturnsFormattedString()
    {
        var field = new FieldSchema
        {
            LogicalName = "accountid",
            Type = "uniqueidentifier"
        };

        var result = field.ToString();

        result.Should().Be("accountid (uniqueidentifier)");
    }

    [Fact]
    public void Precision_CanBeSetForDecimalFields()
    {
        var field = new FieldSchema
        {
            Type = "decimal",
            Precision = 2
        };

        field.Precision.Should().Be(2);
    }

    [Fact]
    public void IsFileColumn_ReturnsTrueForFileType()
    {
        var field = new FieldSchema { Type = "file" };

        field.IsFileColumn.Should().BeTrue();
    }

    [Fact]
    public void IsFileColumn_IsCaseInsensitive()
    {
        var field = new FieldSchema { Type = "File" };

        field.IsFileColumn.Should().BeTrue();
    }

    [Fact]
    public void IsFileColumn_ReturnsFalseForStringType()
    {
        var field = new FieldSchema { Type = "string" };

        field.IsFileColumn.Should().BeFalse();
    }

    [Fact]
    public void MaxFileSizeKB_DefaultsToNull()
    {
        var field = new FieldSchema();

        field.MaxFileSizeKB.Should().BeNull();
    }

    [Fact]
    public void MaxFileSizeKB_CanBeSet()
    {
        var field = new FieldSchema { MaxFileSizeKB = 32768 };

        field.MaxFileSizeKB.Should().Be(32768);
    }
}

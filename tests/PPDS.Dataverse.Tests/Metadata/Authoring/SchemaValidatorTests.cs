using FluentAssertions;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata.Authoring;

[Trait("Category", "Unit")]
public class SchemaValidatorTests
{
    private readonly SchemaValidator _validator = new();

    #region ValidateSchemaName

    [Theory]
    [InlineData("new_MyTable")]
    [InlineData("cr123_Account")]
    [InlineData("A")]
    [InlineData("abc_def_ghi")]
    [InlineData("Test123")]
    public void ValidateSchemaName_ValidNames_DoesNotThrow(string schemaName)
    {
        var act = () => _validator.ValidateSchemaName(schemaName);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("123_Invalid", "starts with number")]
    [InlineData("has space", "contains space")]
    [InlineData("has-hyphen", "contains hyphen")]
    [InlineData("has.dot", "contains dot")]
    [InlineData("has@symbol", "contains special character")]
    [InlineData("has!bang", "contains special character")]
    public void ValidateSchemaName_InvalidNames_ThrowsWithInvalidSchemaName(string schemaName, string reason)
    {
        _ = reason; // used for test identification only

        var act = () => _validator.ValidateSchemaName(schemaName);

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.InvalidSchemaName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSchemaName_NullOrEmpty_ThrowsWithMissingRequiredField(string? schemaName)
    {
        var act = () => _validator.ValidateSchemaName(schemaName!);

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.MissingRequiredField);
    }

    #endregion

    #region ValidatePrefix

    [Fact]
    public void ValidatePrefix_MatchingPrefix_DoesNotThrow()
    {
        var act = () => _validator.ValidatePrefix("new_MyTable", "new");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidatePrefix_CaseInsensitive_DoesNotThrow()
    {
        var act = () => _validator.ValidatePrefix("NEW_MyTable", "new");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidatePrefix_WrongPrefix_ThrowsWithInvalidPrefix()
    {
        var act = () => _validator.ValidatePrefix("other_MyTable", "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.InvalidPrefix);
    }

    [Fact]
    public void ValidatePrefix_NoUnderscore_ThrowsWithInvalidPrefix()
    {
        var act = () => _validator.ValidatePrefix("newMyTable", "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.InvalidPrefix);
    }

    #endregion

    #region ValidateRequiredString

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequiredString_Empty_ThrowsWithMissingRequiredField(string? value)
    {
        var act = () => _validator.ValidateRequiredString(value, "TestField");

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.MissingRequiredField);
    }

    [Fact]
    public void ValidateRequiredString_ValidValue_DoesNotThrow()
    {
        var act = () => _validator.ValidateRequiredString("valid", "TestField");

        act.Should().NotThrow();
    }

    #endregion

    #region ValidateCreateTableRequest

    [Fact]
    public void ValidateCreateTableRequest_ValidRequest_DoesNotThrow()
    {
        var request = new CreateTableRequest
        {
            SolutionUniqueName = "MySolution",
            SchemaName = "new_MyTable",
            DisplayName = "My Table",
            PluralDisplayName = "My Tables",
            OwnershipType = "UserOwned"
        };

        var act = () => _validator.ValidateCreateTableRequest(request, "new");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateCreateTableRequest_MissingDisplayName_ThrowsWithAllErrors()
    {
        var request = new CreateTableRequest
        {
            SolutionUniqueName = "MySolution",
            SchemaName = "new_MyTable",
            DisplayName = "",
            PluralDisplayName = "My Tables"
        };

        var act = () => _validator.ValidateCreateTableRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Field == "DisplayName");
    }

    [Fact]
    public void ValidateCreateTableRequest_InvalidOwnershipType_ThrowsWithInvalidConstraint()
    {
        var request = new CreateTableRequest
        {
            SolutionUniqueName = "MySolution",
            SchemaName = "new_MyTable",
            DisplayName = "My Table",
            PluralDisplayName = "My Tables",
            OwnershipType = "Invalid"
        };

        var act = () => _validator.ValidateCreateTableRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.InvalidConstraint);
    }

    #endregion

    #region ValidateCreateColumnRequest

    [Fact]
    public void ValidateCreateColumnRequest_LookupType_ThrowsWithUseRelationshipForLookup()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyLookup",
            DisplayName = "My Lookup",
            ColumnType = SchemaColumnType.Lookup
        };

        var act = () => _validator.ValidateCreateColumnRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.UseRelationshipForLookup);
    }

    [Fact]
    public void ValidateCreateColumnRequest_StringWithMaxLengthLessThanOne_ThrowsWithInvalidConstraint()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyField",
            DisplayName = "My Field",
            ColumnType = SchemaColumnType.String,
            MaxLength = 0
        };

        var act = () => _validator.ValidateCreateColumnRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.InvalidConstraint);
    }

    [Fact]
    public void ValidateCreateColumnRequest_MemoWithMaxLengthLessThanOne_ThrowsWithInvalidConstraint()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyMemo",
            DisplayName = "My Memo",
            ColumnType = SchemaColumnType.Memo,
            MaxLength = -1
        };

        var act = () => _validator.ValidateCreateColumnRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.InvalidConstraint);
    }

    [Fact]
    public void ValidateCreateColumnRequest_MinValueGreaterThanMaxValue_ThrowsWithInvalidConstraint()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyInt",
            DisplayName = "My Int",
            ColumnType = SchemaColumnType.Integer,
            MinValue = 100,
            MaxValue = 10
        };

        var act = () => _validator.ValidateCreateColumnRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.InvalidConstraint);
    }

    [Fact]
    public void ValidateCreateColumnRequest_DuplicateOptionValues_ThrowsWithDuplicateOptionValue()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyChoice",
            DisplayName = "My Choice",
            ColumnType = SchemaColumnType.Choice,
            Options = new[]
            {
                new OptionDefinition { Label = "Option 1", Value = 1 },
                new OptionDefinition { Label = "Option 2", Value = 1 }
            }
        };

        var act = () => _validator.ValidateCreateColumnRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.DuplicateOptionValue);
    }

    [Fact]
    public void ValidateCreateColumnRequest_ValidStringColumn_DoesNotThrow()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyField",
            DisplayName = "My Field",
            ColumnType = SchemaColumnType.String,
            MaxLength = 200
        };

        var act = () => _validator.ValidateCreateColumnRequest(request, "new");

        act.Should().NotThrow();
    }

    #endregion

    #region ValidateCreateKeyRequest

    [Fact]
    public void ValidateCreateKeyRequest_ZeroAttributes_ThrowsWithInvalidKeyAttributeCount()
    {
        var request = new CreateKeyRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyKey",
            DisplayName = "My Key",
            KeyAttributes = []
        };

        var act = () => _validator.ValidateCreateKeyRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.InvalidKeyAttributeCount);
    }

    [Fact]
    public void ValidateCreateKeyRequest_MoreThan16Attributes_ThrowsWithInvalidKeyAttributeCount()
    {
        var attributes = Enumerable.Range(1, 17).Select(i => $"attr{i}").ToArray();
        var request = new CreateKeyRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyKey",
            DisplayName = "My Key",
            KeyAttributes = attributes
        };

        var act = () => _validator.ValidateCreateKeyRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.InvalidKeyAttributeCount);
    }

    [Fact]
    public void ValidateCreateKeyRequest_OneAttribute_DoesNotThrow()
    {
        var request = new CreateKeyRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyKey",
            DisplayName = "My Key",
            KeyAttributes = ["new_field1"]
        };

        var act = () => _validator.ValidateCreateKeyRequest(request, "new");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateCreateKeyRequest_SixteenAttributes_DoesNotThrow()
    {
        var attributes = Enumerable.Range(1, 16).Select(i => $"attr{i}").ToArray();
        var request = new CreateKeyRequest
        {
            SolutionUniqueName = "MySolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyKey",
            DisplayName = "My Key",
            KeyAttributes = attributes
        };

        var act = () => _validator.ValidateCreateKeyRequest(request, "new");

        act.Should().NotThrow();
    }

    #endregion

    #region ValidateCreateOneToManyRequest

    [Fact]
    public void ValidateCreateOneToManyRequest_ValidRequest_DoesNotThrow()
    {
        var request = new CreateOneToManyRequest
        {
            SolutionUniqueName = "MySolution",
            ReferencedEntity = "account",
            ReferencingEntity = "contact",
            SchemaName = "new_account_contact",
            LookupSchemaName = "new_AccountId",
            LookupDisplayName = "Account"
        };

        var act = () => _validator.ValidateCreateOneToManyRequest(request, "new");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateCreateOneToManyRequest_MissingReferencedEntity_Throws()
    {
        var request = new CreateOneToManyRequest
        {
            SolutionUniqueName = "MySolution",
            ReferencedEntity = "",
            ReferencingEntity = "contact",
            SchemaName = "new_account_contact",
            LookupSchemaName = "new_AccountId",
            LookupDisplayName = "Account"
        };

        var act = () => _validator.ValidateCreateOneToManyRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Field == "ReferencedEntity");
    }

    #endregion

    #region ValidateCreateManyToManyRequest

    [Fact]
    public void ValidateCreateManyToManyRequest_ValidRequest_DoesNotThrow()
    {
        var request = new CreateManyToManyRequest
        {
            SolutionUniqueName = "MySolution",
            Entity1LogicalName = "account",
            Entity2LogicalName = "contact",
            SchemaName = "new_account_contact_mm"
        };

        var act = () => _validator.ValidateCreateManyToManyRequest(request, "new");

        act.Should().NotThrow();
    }

    #endregion

    #region ValidateCreateGlobalChoiceRequest

    [Fact]
    public void ValidateCreateGlobalChoiceRequest_ValidRequest_DoesNotThrow()
    {
        var request = new CreateGlobalChoiceRequest
        {
            SolutionUniqueName = "MySolution",
            SchemaName = "new_MyChoice",
            DisplayName = "My Choice",
            Options = new[]
            {
                new OptionDefinition { Label = "Option 1", Value = 100000 },
                new OptionDefinition { Label = "Option 2", Value = 100001 }
            }
        };

        var act = () => _validator.ValidateCreateGlobalChoiceRequest(request, "new");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateCreateGlobalChoiceRequest_DuplicateOptionValues_Throws()
    {
        var request = new CreateGlobalChoiceRequest
        {
            SolutionUniqueName = "MySolution",
            SchemaName = "new_MyChoice",
            DisplayName = "My Choice",
            Options = new[]
            {
                new OptionDefinition { Label = "Option 1", Value = 100000 },
                new OptionDefinition { Label = "Option 2", Value = 100000 }
            }
        };

        var act = () => _validator.ValidateCreateGlobalChoiceRequest(request, "new");

        act.Should().Throw<MetadataValidationException>()
            .Which.ValidationMessages.Should().Contain(m => m.Rule == MetadataErrorCodes.DuplicateOptionValue);
    }

    #endregion

    #region Multiple Errors Collected

    [Fact]
    public void ValidateCreateTableRequest_MultipleErrors_CollectsAll()
    {
        var request = new CreateTableRequest
        {
            SolutionUniqueName = "",
            SchemaName = "123invalid",
            DisplayName = "",
            PluralDisplayName = ""
        };

        var act = () => _validator.ValidateCreateTableRequest(request, "new");

        var ex = act.Should().Throw<MetadataValidationException>().Which;
        ex.ValidationMessages.Count.Should().BeGreaterThan(1);
    }

    #endregion
}

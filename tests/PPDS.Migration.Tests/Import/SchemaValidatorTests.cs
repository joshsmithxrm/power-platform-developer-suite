using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class SchemaValidatorTests
{
    private readonly Mock<IDataverseConnectionPool> _connectionPool;
    private readonly SchemaValidator _sut;

    public SchemaValidatorTests()
    {
        _connectionPool = new Mock<IDataverseConnectionPool>();
        _sut = new SchemaValidator(_connectionPool.Object);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionPoolIsNull()
    {
        var act = () => new SchemaValidator(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connectionPool");
    }

    #region DetectMissingColumns

    [Fact]
    public void DetectMissingColumns_ReturnsEmpty_WhenAllColumnsExist()
    {
        // Arrange
        var record = new Entity("account");
        record["name"] = "Test";
        record["revenue"] = 100m;

        var data = new MigrationData
        {
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = new List<Entity> { record }
            }
        };

        var targetMetadata = CreateFieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>
        {
            ["account"] = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = new FieldValidity(true, true),
                ["revenue"] = new FieldValidity(true, true)
            }
        });

        // Act
        var result = _sut.DetectMissingColumns(data, targetMetadata);

        // Assert
        result.HasMissingColumns.Should().BeFalse();
        result.TotalMissingCount.Should().Be(0);
    }

    [Fact]
    public void DetectMissingColumns_ReturnsMissing_WhenTargetLacksColumn()
    {
        // Arrange
        var record = new Entity("account");
        record["name"] = "Test";
        record["custom_field"] = "value";

        var data = new MigrationData
        {
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = new List<Entity> { record }
            }
        };

        var targetMetadata = CreateFieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>
        {
            ["account"] = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = new FieldValidity(true, true)
            }
        });

        // Act
        var result = _sut.DetectMissingColumns(data, targetMetadata);

        // Assert
        result.HasMissingColumns.Should().BeTrue();
        result.TotalMissingCount.Should().Be(1);
        result.MissingColumns.Should().ContainKey("account");
        result.MissingColumns["account"].Should().Contain("custom_field");
    }

    [Fact]
    public void DetectMissingColumns_SkipsEntitiesWithNoRecords()
    {
        // Arrange
        var data = new MigrationData
        {
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = new List<Entity>()
            }
        };

        var targetMetadata = CreateFieldMetadataCollection(
            new Dictionary<string, Dictionary<string, FieldValidity>>());

        // Act
        var result = _sut.DetectMissingColumns(data, targetMetadata);

        // Assert
        result.HasMissingColumns.Should().BeFalse();
    }

    [Fact]
    public void DetectMissingColumns_AggregatesFieldsAcrossRecords()
    {
        // Arrange
        var record1 = new Entity("account");
        record1["name"] = "Test";

        var record2 = new Entity("account");
        record2["missing_field"] = "value";

        var data = new MigrationData
        {
            EntityData = new Dictionary<string, IReadOnlyList<Entity>>
            {
                ["account"] = new List<Entity> { record1, record2 }
            }
        };

        var targetMetadata = CreateFieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>
        {
            ["account"] = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = new FieldValidity(true, true)
            }
        });

        // Act
        var result = _sut.DetectMissingColumns(data, targetMetadata);

        // Assert
        result.HasMissingColumns.Should().BeTrue();
        result.MissingColumns["account"].Should().Contain("missing_field");
    }

    #endregion

    #region ShouldIncludeField

    [Fact]
    public void ShouldIncludeField_ReturnsTrue_WhenFieldIsValidForCreateAndUpdate()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new FieldValidity(true, true)
        };

        // Act
        var result = _sut.ShouldIncludeField("name", ImportMode.Upsert, metadata, out var reason);

        // Assert
        result.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void ShouldIncludeField_ExcludesNonCreatableFields_OnCreate()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["createdon"] = new FieldValidity(false, false)
        };

        // Act
        var result = _sut.ShouldIncludeField("createdon", ImportMode.Create, metadata, out var reason);

        // Assert
        result.Should().BeFalse();
        reason.Should().NotBeNull();
    }

    [Fact]
    public void ShouldIncludeField_ExcludesNonUpdatableFields_OnUpdate()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["createonly"] = new FieldValidity(true, false)
        };

        // Act
        var result = _sut.ShouldIncludeField("createonly", ImportMode.Update, metadata, out var reason);

        // Assert
        result.Should().BeFalse();
        reason.Should().Be("not valid for update");
    }

    [Fact]
    public void ShouldIncludeField_ReturnsFalse_WhenFieldNotInMetadata()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = new FieldValidity(true, true)
        };

        // Act
        var result = _sut.ShouldIncludeField("unknown_field", ImportMode.Upsert, metadata, out var reason);

        // Assert
        result.Should().BeFalse();
        reason.Should().Be("not found in target");
    }

    [Fact]
    public void ShouldIncludeField_ReturnsFalse_WhenMetadataIsNull()
    {
        // Act
        var result = _sut.ShouldIncludeField("name", ImportMode.Upsert, null, out var reason);

        // Assert
        result.Should().BeFalse();
        reason.Should().Be("not found in target");
    }

    [Fact]
    public void ShouldIncludeField_ReturnsFalse_WhenNotValidForCreateOrUpdate()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["readonly"] = new FieldValidity(false, false)
        };

        // Act
        var result = _sut.ShouldIncludeField("readonly", ImportMode.Upsert, metadata, out var reason);

        // Assert
        result.Should().BeFalse();
        reason.Should().Be("not valid for create or update");
    }

    [Fact]
    public void ShouldIncludeField_IncludesCreateOnlyField_OnCreate()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["createonly"] = new FieldValidity(true, false)
        };

        // Act
        var result = _sut.ShouldIncludeField("createonly", ImportMode.Create, metadata, out var reason);

        // Assert
        result.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void ShouldIncludeField_IncludesUpdateOnlyField_OnUpdate()
    {
        // Arrange
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["updateonly"] = new FieldValidity(false, true)
        };

        // Act
        var result = _sut.ShouldIncludeField("updateonly", ImportMode.Update, metadata, out var reason);

        // Assert
        result.Should().BeTrue();
        reason.Should().BeNull();
    }

    [Fact]
    public void ShouldIncludeField_IncludesFieldValidForEither_OnUpsert()
    {
        // Arrange - field valid for create only
        var metadata = new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase)
        {
            ["createonly"] = new FieldValidity(true, false)
        };

        // Act
        var result = _sut.ShouldIncludeField("createonly", ImportMode.Upsert, metadata, out var reason);

        // Assert
        result.Should().BeTrue();
        reason.Should().BeNull();
    }

    #endregion

    private static FieldMetadataCollection CreateFieldMetadataCollection(
        Dictionary<string, Dictionary<string, FieldValidity>> metadata)
    {
        return new FieldMetadataCollection(metadata);
    }
}

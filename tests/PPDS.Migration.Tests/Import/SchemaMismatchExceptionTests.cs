using FluentAssertions;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class SchemaMismatchExceptionTests
{
    [Fact]
    public void TotalMissingCount_ReturnsSumAcrossAllEntities()
    {
        // Arrange
        var missingColumns = new Dictionary<string, List<string>>
        {
            ["account"] = new List<string> { "custom_field1", "custom_field2" },
            ["contact"] = new List<string> { "custom_field3" }
        };

        // Act
        var exception = new SchemaMismatchException("Schema mismatch", missingColumns);

        // Assert
        exception.TotalMissingCount.Should().Be(3);
    }

    [Fact]
    public void TotalMissingCount_ReturnsZero_WhenDictionaryIsEmpty()
    {
        // Arrange
        var missingColumns = new Dictionary<string, List<string>>();

        // Act
        var exception = new SchemaMismatchException("Schema mismatch", missingColumns);

        // Assert
        exception.TotalMissingCount.Should().Be(0);
    }

    [Fact]
    public void MissingColumns_IsSetCorrectly()
    {
        // Arrange
        var missingColumns = new Dictionary<string, List<string>>
        {
            ["account"] = new List<string> { "custom_field1" }
        };

        // Act
        var exception = new SchemaMismatchException("Schema mismatch", missingColumns);

        // Assert
        exception.MissingColumns.Should().ContainKey("account");
        exception.MissingColumns["account"].Should().ContainSingle().Which.Should().Be("custom_field1");
    }

    [Fact]
    public void TotalMissingCount_ReturnsSumAcrossAllEntities_WhenConstructedWithInnerException()
    {
        // Arrange
        var missingColumns = new Dictionary<string, List<string>>
        {
            ["account"] = new List<string> { "field_a", "field_b" },
            ["lead"] = new List<string> { "field_c" }
        };
        var inner = new InvalidOperationException("inner");

        // Act
        var exception = new SchemaMismatchException("Schema mismatch", missingColumns, inner);

        // Assert
        exception.TotalMissingCount.Should().Be(3);
        exception.InnerException.Should().BeSameAs(inner);
    }
}

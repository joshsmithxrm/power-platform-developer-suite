using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

/// <summary>
/// Tests for BulkOperationOptions.
/// </summary>
public class BulkOperationOptionsTests
{
    #region Default Values Tests

    [Fact]
    public void DefaultBatchSize_Is100()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.BatchSize.Should().Be(100);
    }

    [Fact]
    public void DefaultElasticTable_IsFalse()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.ElasticTable.Should().BeFalse();
    }

    [Fact]
    public void DefaultContinueOnError_IsTrue()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.ContinueOnError.Should().BeTrue();
    }

    [Fact]
    public void DefaultBypassCustomLogic_IsNone()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.BypassCustomLogic.Should().Be(CustomLogicBypass.None);
    }

    [Fact]
    public void DefaultBypassPowerAutomateFlows_IsFalse()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.BypassPowerAutomateFlows.Should().BeFalse();
    }

    [Fact]
    public void DefaultSuppressDuplicateDetection_IsFalse()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.SuppressDuplicateDetection.Should().BeFalse();
    }

    [Fact]
    public void DefaultTag_IsNull()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.Tag.Should().BeNull();
    }

    [Fact]
    public void DefaultMaxParallelBatches_IsNull()
    {
        // Act
        var options = new BulkOperationOptions();

        // Assert
        options.MaxParallelBatches.Should().BeNull();
    }

    #endregion

    #region Property Setting Tests

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(1000)]
    public void BatchSize_CanBeSet(int batchSize)
    {
        // Act
        var options = new BulkOperationOptions { BatchSize = batchSize };

        // Assert
        options.BatchSize.Should().Be(batchSize);
    }

    [Fact]
    public void ElasticTable_CanBeSetToTrue()
    {
        // Act
        var options = new BulkOperationOptions { ElasticTable = true };

        // Assert
        options.ElasticTable.Should().BeTrue();
    }

    [Fact]
    public void ContinueOnError_CanBeSetToFalse()
    {
        // Act
        var options = new BulkOperationOptions { ContinueOnError = false };

        // Assert
        options.ContinueOnError.Should().BeFalse();
    }

    [Theory]
    [InlineData(CustomLogicBypass.None)]
    [InlineData(CustomLogicBypass.Synchronous)]
    [InlineData(CustomLogicBypass.Asynchronous)]
    [InlineData(CustomLogicBypass.All)]
    public void BypassCustomLogic_CanBeSet(CustomLogicBypass bypass)
    {
        // Act
        var options = new BulkOperationOptions { BypassCustomLogic = bypass };

        // Assert
        options.BypassCustomLogic.Should().Be(bypass);
    }

    [Fact]
    public void BypassPowerAutomateFlows_CanBeSetToTrue()
    {
        // Act
        var options = new BulkOperationOptions { BypassPowerAutomateFlows = true };

        // Assert
        options.BypassPowerAutomateFlows.Should().BeTrue();
    }

    [Fact]
    public void SuppressDuplicateDetection_CanBeSetToTrue()
    {
        // Act
        var options = new BulkOperationOptions { SuppressDuplicateDetection = true };

        // Assert
        options.SuppressDuplicateDetection.Should().BeTrue();
    }

    [Fact]
    public void Tag_CanBeSet()
    {
        // Arrange
        const string tag = "BulkImport-2025-12-28";

        // Act
        var options = new BulkOperationOptions { Tag = tag };

        // Assert
        options.Tag.Should().Be(tag);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(52)]
    public void MaxParallelBatches_CanBeSet(int maxParallel)
    {
        // Act
        var options = new BulkOperationOptions { MaxParallelBatches = maxParallel };

        // Assert
        options.MaxParallelBatches.Should().Be(maxParallel);
    }

    #endregion

    #region Combined Options Tests

    [Fact]
    public void AllBypassOptions_CanBeCombined()
    {
        // Act
        var options = new BulkOperationOptions
        {
            BypassCustomLogic = CustomLogicBypass.All,
            BypassPowerAutomateFlows = true,
            SuppressDuplicateDetection = true
        };

        // Assert
        options.BypassCustomLogic.Should().Be(CustomLogicBypass.All);
        options.BypassPowerAutomateFlows.Should().BeTrue();
        options.SuppressDuplicateDetection.Should().BeTrue();
    }

    [Fact]
    public void ElasticTableOptions_CanBeCombined()
    {
        // Act
        var options = new BulkOperationOptions
        {
            ElasticTable = true,
            BatchSize = 100,
            MaxParallelBatches = 10
        };

        // Assert
        options.ElasticTable.Should().BeTrue();
        options.BatchSize.Should().Be(100);
        options.MaxParallelBatches.Should().Be(10);
    }

    [Fact]
    public void TypicalBulkImportOptions()
    {
        // Act - typical configuration for bulk import
        var options = new BulkOperationOptions
        {
            BatchSize = 100,
            ContinueOnError = true,
            BypassCustomLogic = CustomLogicBypass.Synchronous,
            BypassPowerAutomateFlows = true,
            Tag = "Migration-Q4-2025"
        };

        // Assert
        options.BatchSize.Should().Be(100);
        options.ContinueOnError.Should().BeTrue();
        options.BypassCustomLogic.Should().Be(CustomLogicBypass.Synchronous);
        options.BypassPowerAutomateFlows.Should().BeTrue();
        options.Tag.Should().Be("Migration-Q4-2025");
    }

    #endregion
}

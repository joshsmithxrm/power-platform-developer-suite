using FluentAssertions;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Import;

public class ImportResultTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var result = new ImportResult();

        result.Success.Should().BeFalse();
        result.TiersProcessed.Should().Be(0);
        result.SourceRecordCount.Should().Be(0);
        result.RecordsImported.Should().Be(0);
        result.RecordsUpdated.Should().Be(0);
        result.RelationshipsProcessed.Should().Be(0);
        result.RelationshipsFailed.Should().Be(0);
        result.Duration.Should().Be(TimeSpan.Zero);
        result.Errors.Should().NotBeNull().And.BeEmpty();
        result.EntityResults.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var duration = TimeSpan.FromMinutes(5);
        var errors = new List<MigrationError> { new() { Message = "Test error" } };
        var entityResults = new List<EntityImportResult> { new() { EntityLogicalName = "account" } };

        var result = new ImportResult
        {
            Success = true,
            TiersProcessed = 3,
            SourceRecordCount = 1100,
            RecordsImported = 1000,
            RecordsUpdated = 50,
            RelationshipsProcessed = 10,
            RelationshipsFailed = 2,
            Duration = duration,
            Errors = errors,
            EntityResults = entityResults
        };

        result.Success.Should().BeTrue();
        result.TiersProcessed.Should().Be(3);
        result.SourceRecordCount.Should().Be(1100);
        result.RecordsImported.Should().Be(1000);
        result.RecordsUpdated.Should().Be(50);
        result.RelationshipsProcessed.Should().Be(10);
        result.RelationshipsFailed.Should().Be(2);
        result.Duration.Should().Be(duration);
        result.Errors.Should().HaveCount(1);
        result.EntityResults.Should().HaveCount(1);
    }

    [Fact]
    public void RecordsPerSecond_CalculatesCorrectly()
    {
        var result = new ImportResult
        {
            RecordsImported = 1000,
            Duration = TimeSpan.FromSeconds(10)
        };

        result.RecordsPerSecond.Should().Be(100);
    }

    [Fact]
    public void RecordsPerSecond_ReturnsZeroWhenDurationIsZero()
    {
        var result = new ImportResult
        {
            RecordsImported = 1000,
            Duration = TimeSpan.Zero
        };

        result.RecordsPerSecond.Should().Be(0);
    }
}

public class EntityImportResultTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var result = new EntityImportResult();

        result.EntityLogicalName.Should().BeEmpty();
        result.TierNumber.Should().Be(0);
        result.RecordCount.Should().Be(0);
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.CreatedCount.Should().BeNull();
        result.UpdatedCount.Should().BeNull();
        result.Duration.Should().Be(TimeSpan.Zero);
        result.Success.Should().BeTrue();
        result.Errors.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var duration = TimeSpan.FromSeconds(30);
        var errors = new List<MigrationError> { new() { Message = "Test error" } };

        var result = new EntityImportResult
        {
            EntityLogicalName = "account",
            TierNumber = 1,
            RecordCount = 500,
            SuccessCount = 495,
            FailureCount = 5,
            CreatedCount = 300,
            UpdatedCount = 195,
            Duration = duration,
            Success = false,
            Errors = errors
        };

        result.EntityLogicalName.Should().Be("account");
        result.TierNumber.Should().Be(1);
        result.RecordCount.Should().Be(500);
        result.SuccessCount.Should().Be(495);
        result.FailureCount.Should().Be(5);
        result.CreatedCount.Should().Be(300);
        result.UpdatedCount.Should().Be(195);
        result.Duration.Should().Be(duration);
        result.Success.Should().BeFalse();
        result.Errors.Should().HaveCount(1);
    }
}

using FluentAssertions;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class MigrationResultTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var result = new MigrationResult();

        result.Success.Should().BeFalse();
        result.RecordsProcessed.Should().Be(0);
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
        result.Duration.Should().Be(TimeSpan.Zero);
        result.Errors.Should().NotBeNull().And.BeEmpty();
        result.CreatedCount.Should().BeNull();
        result.UpdatedCount.Should().BeNull();
        result.M2MCount.Should().BeNull();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var duration = TimeSpan.FromMinutes(10);
        var errors = new List<MigrationError> { new() { Message = "Test error" } };

        var result = new MigrationResult
        {
            Success = true,
            RecordsProcessed = 5000,
            SuccessCount = 4950,
            FailureCount = 50,
            Duration = duration,
            Errors = errors,
            CreatedCount = 3000,
            UpdatedCount = 1950,
            M2MCount = 500
        };

        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(5000);
        result.SuccessCount.Should().Be(4950);
        result.FailureCount.Should().Be(50);
        result.Duration.Should().Be(duration);
        result.Errors.Should().HaveCount(1);
        result.CreatedCount.Should().Be(3000);
        result.UpdatedCount.Should().Be(1950);
        result.M2MCount.Should().Be(500);
    }

    [Fact]
    public void RecordsPerSecond_CalculatesCorrectly()
    {
        var result = new MigrationResult
        {
            RecordsProcessed = 6000,
            Duration = TimeSpan.FromSeconds(30)
        };

        result.RecordsPerSecond.Should().Be(200);
    }

    [Fact]
    public void RecordsPerSecond_ReturnsZeroWhenDurationIsZero()
    {
        var result = new MigrationResult
        {
            RecordsProcessed = 1000,
            Duration = TimeSpan.Zero
        };

        result.RecordsPerSecond.Should().Be(0);
    }
}

public class MigrationErrorTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var error = new MigrationError();

        error.Phase.Should().Be(MigrationPhase.Analyzing);
        error.EntityLogicalName.Should().BeNull();
        error.RecordIndex.Should().BeNull();
        error.ErrorCode.Should().BeNull();
        error.Message.Should().BeEmpty();
        error.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var timestamp = DateTime.UtcNow;

        var error = new MigrationError
        {
            Phase = MigrationPhase.Importing,
            EntityLogicalName = "account",
            RecordIndex = 42,
            ErrorCode = -2147220969,
            Message = "The record was not found.",
            Timestamp = timestamp
        };

        error.Phase.Should().Be(MigrationPhase.Importing);
        error.EntityLogicalName.Should().Be("account");
        error.RecordIndex.Should().Be(42);
        error.ErrorCode.Should().Be(-2147220969);
        error.Message.Should().Be("The record was not found.");
        error.Timestamp.Should().Be(timestamp);
    }
}

public class MigrationPhaseTests
{
    [Fact]
    public void MigrationPhase_HasExpectedValues()
    {
        var analyzing = MigrationPhase.Analyzing;
        var exporting = MigrationPhase.Exporting;
        var importing = MigrationPhase.Importing;
        var processingDeferredFields = MigrationPhase.ProcessingDeferredFields;
        var processingRelationships = MigrationPhase.ProcessingRelationships;
        var complete = MigrationPhase.Complete;
        var error = MigrationPhase.Error;

        analyzing.Should().Be(MigrationPhase.Analyzing);
        exporting.Should().Be(MigrationPhase.Exporting);
        importing.Should().Be(MigrationPhase.Importing);
        processingDeferredFields.Should().Be(MigrationPhase.ProcessingDeferredFields);
        processingRelationships.Should().Be(MigrationPhase.ProcessingRelationships);
        complete.Should().Be(MigrationPhase.Complete);
        error.Should().Be(MigrationPhase.Error);
    }
}

using FluentAssertions;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class ProgressEventArgsTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var args = new ProgressEventArgs();

        args.Phase.Should().Be(MigrationPhase.Analyzing);
        args.Entity.Should().BeNull();
        args.Field.Should().BeNull();
        args.Relationship.Should().BeNull();
        args.TierNumber.Should().BeNull();
        args.Current.Should().Be(0);
        args.Total.Should().Be(0);
        args.RecordsPerSecond.Should().BeNull();
        args.EstimatedRemaining.Should().BeNull();
        args.SuccessCount.Should().Be(0);
        args.FailureCount.Should().Be(0);
        args.Message.Should().BeNull();
        args.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var timestamp = DateTime.UtcNow;
        var estimatedRemaining = TimeSpan.FromMinutes(5);

        var args = new ProgressEventArgs
        {
            Phase = MigrationPhase.Importing,
            Entity = "account",
            Field = "primarycontactid",
            Relationship = "systemuserroles_association",
            TierNumber = 2,
            Current = 500,
            Total = 1000,
            RecordsPerSecond = 50.5,
            EstimatedRemaining = estimatedRemaining,
            SuccessCount = 495,
            FailureCount = 5,
            Message = "Processing records",
            Timestamp = timestamp
        };

        args.Phase.Should().Be(MigrationPhase.Importing);
        args.Entity.Should().Be("account");
        args.Field.Should().Be("primarycontactid");
        args.Relationship.Should().Be("systemuserroles_association");
        args.TierNumber.Should().Be(2);
        args.Current.Should().Be(500);
        args.Total.Should().Be(1000);
        args.RecordsPerSecond.Should().Be(50.5);
        args.EstimatedRemaining.Should().Be(estimatedRemaining);
        args.SuccessCount.Should().Be(495);
        args.FailureCount.Should().Be(5);
        args.Message.Should().Be("Processing records");
        args.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void PercentComplete_CalculatesCorrectly()
    {
        var args = new ProgressEventArgs
        {
            Current = 50,
            Total = 200
        };

        args.PercentComplete.Should().Be(25);
    }

    [Fact]
    public void PercentComplete_ReturnsZeroWhenTotalIsZero()
    {
        var args = new ProgressEventArgs
        {
            Current = 50,
            Total = 0
        };

        args.PercentComplete.Should().Be(0);
    }

    [Fact]
    public void PercentComplete_Returns100WhenCurrentEqualsTotal()
    {
        var args = new ProgressEventArgs
        {
            Current = 1000,
            Total = 1000
        };

        args.PercentComplete.Should().Be(100);
    }

    [Fact]
    public void FilterApplied_DefaultsFalse()
    {
        var args = new ProgressEventArgs();

        args.FilterApplied.Should().BeFalse();
        args.FilterDescription.Should().BeNull();
    }

    [Fact]
    public void FilterApplied_CanBeSetAndRetrieved()
    {
        var args = new ProgressEventArgs
        {
            FilterApplied = true,
            FilterDescription = "statecode eq '0'"
        };

        args.FilterApplied.Should().BeTrue();
        args.FilterDescription.Should().Be("statecode eq '0'");
    }
}

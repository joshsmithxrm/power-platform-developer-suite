using System;
using FluentAssertions;
using PPDS.Dataverse.Progress;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

[Trait("Category", "Unit")]
public class ProgressAdapterFactoryTests
{
    [Fact]
    public void Create_ThrowsOnNullReporter()
    {
        var act = () => ProgressAdapterFactory.Create(null!, _ => new ProgressEventArgs());

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("reporter");
    }

    [Fact]
    public void Create_ThrowsOnNullMapper()
    {
        var act = () => ProgressAdapterFactory.Create(
            IProgressReporter.Silent,
            null!);

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("mapper");
    }

    [Fact]
    public void Create_ReturnsNonNullAdapter()
    {
        var adapter = ProgressAdapterFactory.Create(
            IProgressReporter.Silent,
            _ => new ProgressEventArgs());

        adapter.Should().NotBeNull();
    }

    [Fact]
    public void Report_ForwardsSnapshotToReporter()
    {
        // Arrange
        var recorder = new RecordingProgressReporter();
        var adapter = ProgressAdapterFactory.Create(recorder, snapshot => new ProgressEventArgs
        {
            Phase = MigrationPhase.Importing,
            Entity = "account",
            Current = (int)snapshot.Processed,
            Total = (int)snapshot.Total,
            SuccessCount = (int)snapshot.Succeeded,
            FailureCount = (int)snapshot.Failed,
            RecordsPerSecond = snapshot.RatePerSecond,
            EstimatedRemaining = snapshot.EstimatedRemaining
        });

        var snapshot = new ProgressSnapshot
        {
            Succeeded = 75,
            Failed = 5,
            Total = 200,
            OverallRatePerSecond = 42.5,
            EstimatedRemaining = TimeSpan.FromSeconds(10)
        };

        // Act
        adapter.Report(snapshot);

        // Assert
        recorder.Reports.Should().ContainSingle();
        var args = recorder.Reports[0];
        args.Phase.Should().Be(MigrationPhase.Importing);
        args.Entity.Should().Be("account");
        args.Current.Should().Be(80); // Processed = Succeeded + Failed
        args.Total.Should().Be(200);
        args.SuccessCount.Should().Be(75);
        args.FailureCount.Should().Be(5);
        args.RecordsPerSecond.Should().Be(42.5);
        args.EstimatedRemaining.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Report_MapsImportingPhaseWithTierNumber()
    {
        // Arrange
        var recorder = new RecordingProgressReporter();
        var adapter = ProgressAdapterFactory.Create(recorder, snapshot => new ProgressEventArgs
        {
            Phase = MigrationPhase.Importing,
            Entity = "contact",
            TierNumber = 3,
            Current = (int)snapshot.Processed,
            Total = (int)snapshot.Total,
            SuccessCount = (int)snapshot.Succeeded,
            FailureCount = (int)snapshot.Failed,
            RecordsPerSecond = snapshot.RatePerSecond,
            EstimatedRemaining = snapshot.EstimatedRemaining
        });

        // Act
        adapter.Report(new ProgressSnapshot
        {
            Succeeded = 10,
            Total = 50,
            OverallRatePerSecond = 5.0,
            EstimatedRemaining = TimeSpan.FromSeconds(8)
        });

        // Assert
        recorder.Reports.Should().ContainSingle();
        var args = recorder.Reports[0];
        args.TierNumber.Should().Be(3);
        args.Entity.Should().Be("contact");
    }

    [Fact]
    public void Report_MapsDeferredFieldsPhase()
    {
        // Arrange
        var recorder = new RecordingProgressReporter();
        var fieldList = "parentid, managerid";
        var adapter = ProgressAdapterFactory.Create(recorder, snapshot => new ProgressEventArgs
        {
            Phase = MigrationPhase.ProcessingDeferredFields,
            Entity = "account",
            Field = fieldList,
            Current = (int)snapshot.Processed,
            Total = 100,
            SuccessCount = (int)snapshot.Succeeded,
            Message = $"Updating deferred fields: {fieldList}"
        });

        // Act
        adapter.Report(new ProgressSnapshot
        {
            Succeeded = 30,
            Total = 100,
            OverallRatePerSecond = 15.0,
            EstimatedRemaining = TimeSpan.FromSeconds(4)
        });

        // Assert
        recorder.Reports.Should().ContainSingle();
        var args = recorder.Reports[0];
        args.Phase.Should().Be(MigrationPhase.ProcessingDeferredFields);
        args.Field.Should().Be("parentid, managerid");
        args.Message.Should().Be("Updating deferred fields: parentid, managerid");
        args.Current.Should().Be(30);
        args.Total.Should().Be(100);
        args.SuccessCount.Should().Be(30);
    }

    [Fact]
    public void Report_MultipleReportsAreAllForwarded()
    {
        // Arrange
        var recorder = new RecordingProgressReporter();
        var adapter = ProgressAdapterFactory.Create(recorder, snapshot => new ProgressEventArgs
        {
            Phase = MigrationPhase.Importing,
            Current = (int)snapshot.Processed,
            Total = (int)snapshot.Total
        });

        // Act
        adapter.Report(new ProgressSnapshot { Succeeded = 10, Total = 100 });
        adapter.Report(new ProgressSnapshot { Succeeded = 50, Total = 100 });
        adapter.Report(new ProgressSnapshot { Succeeded = 100, Total = 100 });

        // Assert
        recorder.Reports.Should().HaveCount(3);
        recorder.Reports[0].Current.Should().Be(10);
        recorder.Reports[1].Current.Should().Be(50);
        recorder.Reports[2].Current.Should().Be(100);
    }

    /// <summary>
    /// A simple recording implementation of <see cref="IProgressReporter"/>
    /// that stores all reported events for assertion.
    /// </summary>
    private sealed class RecordingProgressReporter : IProgressReporter
    {
        public string OperationName { get; set; } = string.Empty;
        public System.Collections.Generic.List<ProgressEventArgs> Reports { get; } = new();

        public void Report(ProgressEventArgs args) => Reports.Add(args);
        public void Complete(MigrationResult result) { }
        public void Error(Exception exception, string? context = null) { }
        public void Reset() { }
    }
}

using FluentAssertions;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

[Trait("Category", "Unit")]
public class SilentProgressReporterTests
{
    [Fact]
    public void Silent_IsNotNull()
    {
        IProgressReporter.Silent.Should().NotBeNull();
    }

    [Fact]
    public void Silent_ReturnsSameInstance()
    {
        var first = IProgressReporter.Silent;
        var second = IProgressReporter.Silent;

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Report_DoesNotThrow()
    {
        var silent = IProgressReporter.Silent;

        var act = () => silent.Report(new ProgressEventArgs
        {
            Phase = MigrationPhase.Importing,
            Entity = "account",
            Current = 50,
            Total = 100,
            Message = "test"
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void Complete_DoesNotThrow()
    {
        var silent = IProgressReporter.Silent;

        var act = () => silent.Complete(new MigrationResult
        {
            Success = true,
            RecordsProcessed = 100,
            SuccessCount = 100
        });

        act.Should().NotThrow();
    }

    [Fact]
    public void Error_DoesNotThrow()
    {
        var silent = IProgressReporter.Silent;

        var act = () => silent.Error(new InvalidOperationException("test error"), "test context");

        act.Should().NotThrow();
    }

    [Fact]
    public void Error_WithNullContext_DoesNotThrow()
    {
        var silent = IProgressReporter.Silent;

        var act = () => silent.Error(new InvalidOperationException("test error"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Reset_DoesNotThrow()
    {
        var silent = IProgressReporter.Silent;

        var act = () => silent.Reset();

        act.Should().NotThrow();
    }

    [Fact]
    public void OperationName_CanBeSetAndRead()
    {
        var silent = IProgressReporter.Silent;
        var original = silent.OperationName;

        try
        {
            silent.OperationName = "Export";
            silent.OperationName.Should().Be("Export");
        }
        finally
        {
            silent.OperationName = original;
        }
    }

    [Fact]
    public void OperationName_CanBeSetToEmpty()
    {
        var silent = IProgressReporter.Silent;
        var original = silent.OperationName;

        try
        {
            silent.OperationName = "SomeValue";
            silent.OperationName = string.Empty;
            silent.OperationName.Should().BeEmpty();
        }
        finally
        {
            silent.OperationName = original;
        }
    }
}

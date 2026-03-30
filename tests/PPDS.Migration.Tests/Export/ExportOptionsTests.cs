using FluentAssertions;
using PPDS.Migration.Export;
using Xunit;

namespace PPDS.Migration.Tests.Export;

public class ExportOptionsTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var options = new ExportOptions();

        options.DegreeOfParallelism.Should().Be(Environment.ProcessorCount * 2);
        options.PageSize.Should().Be(5000);
        options.ProgressInterval.Should().Be(100);
    }

    [Fact]
    public void DegreeOfParallelism_CanBeSet()
    {
        var options = new ExportOptions { DegreeOfParallelism = 8 };

        options.DegreeOfParallelism.Should().Be(8);
    }

    [Fact]
    public void PageSize_CanBeSet()
    {
        var options = new ExportOptions { PageSize = 1000 };

        options.PageSize.Should().Be(1000);
    }

    [Fact]
    public void ProgressInterval_CanBeSet()
    {
        var options = new ExportOptions { ProgressInterval = 500 };

        options.ProgressInterval.Should().Be(500);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IncludeFileData_DefaultsFalse()
    {
        var options = new ExportOptions();

        options.IncludeFileData.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IncludeFileData_CanBeEnabled()
    {
        var options = new ExportOptions { IncludeFileData = true };

        options.IncludeFileData.Should().BeTrue();
    }

    [Fact]
    public void PageLevelParallelism_DefaultsToZero()
    {
        var options = new ExportOptions();

        options.PageLevelParallelism.Should().Be(0);
    }

    [Fact]
    public void PageLevelParallelismThreshold_DefaultsTo5000()
    {
        var options = new ExportOptions();

        options.PageLevelParallelismThreshold.Should().Be(5000);
    }

    [Fact]
    public void PageLevelParallelism_CanBeSet()
    {
        var options = new ExportOptions { PageLevelParallelism = 8 };

        options.PageLevelParallelism.Should().Be(8);
    }

    [Fact]
    public void PageLevelParallelismThreshold_CanBeSet()
    {
        var options = new ExportOptions { PageLevelParallelismThreshold = 10000 };

        options.PageLevelParallelismThreshold.Should().Be(10000);
    }
}

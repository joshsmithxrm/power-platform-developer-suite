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
}

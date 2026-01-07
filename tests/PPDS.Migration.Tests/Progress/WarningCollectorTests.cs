using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class WarningCollectorTests
{
    [Fact]
    public void AddWarning_SingleWarning_IsCollected()
    {
        var collector = new WarningCollector();
        var warning = new ImportWarning
        {
            Code = ImportWarningCodes.BulkNotSupported,
            Entity = "account",
            Message = "Entity does not support bulk operations",
            Impact = "Reduced throughput"
        };

        collector.AddWarning(warning);

        var warnings = collector.GetWarnings();
        Assert.Single(warnings);
        Assert.Equal(warning.Code, warnings[0].Code);
        Assert.Equal(warning.Entity, warnings[0].Entity);
        Assert.Equal(warning.Message, warnings[0].Message);
        Assert.Equal(warning.Impact, warnings[0].Impact);
    }

    [Fact]
    public void AddWarning_MultipleWarnings_AllCollected()
    {
        var collector = new WarningCollector();

        collector.AddWarning(new ImportWarning
        {
            Code = ImportWarningCodes.BulkNotSupported,
            Entity = "account",
            Message = "Bulk not supported"
        });
        collector.AddWarning(new ImportWarning
        {
            Code = ImportWarningCodes.ColumnSkipped,
            Entity = "contact",
            Message = "Column skipped"
        });

        var warnings = collector.GetWarnings();
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void Count_ReturnsNumberOfWarnings()
    {
        var collector = new WarningCollector();

        Assert.Equal(0, collector.Count);

        collector.AddWarning(new ImportWarning { Code = "TEST1", Message = "Test 1" });
        Assert.Equal(1, collector.Count);

        collector.AddWarning(new ImportWarning { Code = "TEST2", Message = "Test 2" });
        Assert.Equal(2, collector.Count);
    }

    [Fact]
    public void GetWarnings_ReturnsReadOnlyList()
    {
        var collector = new WarningCollector();
        collector.AddWarning(new ImportWarning
        {
            Code = ImportWarningCodes.SchemaMismatch,
            Message = "Schema mismatch detected"
        });

        var warnings = collector.GetWarnings();

        Assert.IsAssignableFrom<IReadOnlyList<ImportWarning>>(warnings);
    }

    [Fact]
    public void GetWarnings_EmptyCollector_ReturnsEmptyList()
    {
        var collector = new WarningCollector();

        var warnings = collector.GetWarnings();

        Assert.Empty(warnings);
    }

    [Fact]
    public void AddWarning_ConcurrentAdds_AllCollected()
    {
        var collector = new WarningCollector();
        const int warningCount = 100;

        Parallel.For(0, warningCount, i =>
        {
            collector.AddWarning(new ImportWarning
            {
                Code = $"TEST_{i}",
                Message = $"Warning {i}"
            });
        });

        Assert.Equal(warningCount, collector.Count);
        Assert.Equal(warningCount, collector.GetWarnings().Count);
    }
}

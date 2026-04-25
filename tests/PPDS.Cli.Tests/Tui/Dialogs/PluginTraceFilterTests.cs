using PPDS.Cli.Services.PluginTraces;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

[Trait("Category", "TuiUnit")]
public sealed class PluginTraceFilterTests
{
    [Fact]
    public void DefaultFilter_AllPropertiesNull()
    {
        var filter = new PluginTraceFilter();

        Assert.Null(filter.TypeName);
        Assert.Null(filter.MessageName);
        Assert.Null(filter.PrimaryEntity);
        Assert.Null(filter.Mode);
        Assert.Null(filter.HasException);
        Assert.Null(filter.MinDurationMs);
        Assert.Null(filter.MaxDurationMs);
        Assert.Null(filter.OperationType);
        Assert.Null(filter.CorrelationId);
        Assert.Null(filter.OrderBy);
    }

    [Fact]
    public void Filter_WithTypeName()
    {
        var filter = new PluginTraceFilter { TypeName = "MyPlugin.PreCreate" };

        Assert.Equal("MyPlugin.PreCreate", filter.TypeName);
        Assert.Null(filter.MessageName);
    }

    [Fact]
    public void Filter_WithSynchronousMode()
    {
        var filter = new PluginTraceFilter { Mode = PluginTraceMode.Synchronous };

        Assert.Equal(PluginTraceMode.Synchronous, filter.Mode);
    }

    [Fact]
    public void Filter_WithAsynchronousMode()
    {
        var filter = new PluginTraceFilter { Mode = PluginTraceMode.Asynchronous };

        Assert.Equal(PluginTraceMode.Asynchronous, filter.Mode);
    }

    [Fact]
    public void Filter_ErrorsOnly()
    {
        var filter = new PluginTraceFilter { HasException = true };

        Assert.True(filter.HasException);
    }

    [Fact]
    public void Filter_WithMinDuration()
    {
        var filter = new PluginTraceFilter { MinDurationMs = 500 };

        Assert.Equal(500, filter.MinDurationMs);
    }

    [Fact]
    public void Filter_WithAllDialogFields()
    {
        var filter = new PluginTraceFilter
        {
            TypeName = "MyPlugin.PostUpdate",
            MessageName = "Update",
            PrimaryEntity = "account",
            Mode = PluginTraceMode.Synchronous,
            HasException = true,
            MinDurationMs = 100
        };

        Assert.Equal("MyPlugin.PostUpdate", filter.TypeName);
        Assert.Equal("Update", filter.MessageName);
        Assert.Equal("account", filter.PrimaryEntity);
        Assert.Equal(PluginTraceMode.Synchronous, filter.Mode);
        Assert.True(filter.HasException);
        Assert.Equal(100, filter.MinDurationMs);
    }

    [Fact]
    public void Filter_RecordEquality()
    {
        var a = new PluginTraceFilter { TypeName = "Test", Mode = PluginTraceMode.Synchronous };
        var b = new PluginTraceFilter { TypeName = "Test", Mode = PluginTraceMode.Synchronous };

        Assert.Equal(a, b);
    }

    [Fact]
    public void Filter_RecordInequality_DifferentMode()
    {
        var a = new PluginTraceFilter { Mode = PluginTraceMode.Synchronous };
        var b = new PluginTraceFilter { Mode = PluginTraceMode.Asynchronous };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Filter_WithDateRange()
    {
        var after = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var before = new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc);

        var filter = new PluginTraceFilter
        {
            CreatedAfter = after,
            CreatedBefore = before
        };

        Assert.Equal(after, filter.CreatedAfter);
        Assert.Equal(before, filter.CreatedBefore);
    }

    [Fact]
    public void Filter_WithCorrelationId()
    {
        var id = Guid.NewGuid();
        var filter = new PluginTraceFilter { CorrelationId = id };

        Assert.Equal(id, filter.CorrelationId);
    }
}

using Moq;
using PPDS.Cli.Services.PluginTraces;

namespace PPDS.Cli.Tests.Services.PluginTraces;

/// <summary>A clock fixed at a start time, optionally moving on by a step at every read.</summary>
internal sealed class TestClock : TimeProvider
{
    private readonly TimeSpan _step;
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _now;

    public TestClock(DateTime startUtc, TimeSpan? step = null, TimeZoneInfo? zone = null)
    {
        _now = new DateTimeOffset(startUtc, TimeSpan.Zero);
        _step = step ?? TimeSpan.Zero;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow()
    {
        var now = _now;
        _now += _step;
        return now;
    }
}

/// <summary>A strict trace service that records the count filters and delete cut-offs.</summary>
internal sealed class FlowFake
{
    public FlowFake(int count)
    {
        Mock = new Mock<IPluginTraceService>(MockBehavior.Strict);
        Mock.Setup(t => t.CountAsync(It.IsAny<PluginTraceFilter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PluginTraceFilter? f, CancellationToken _) => { CountFilters.Add(f!); return count; });
        Mock.Setup(t => t.DeleteCreatedBeforeAsync(It.IsAny<DateTime>(), It.IsAny<IProgress<int>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime cutoff, IProgress<int>? _, CancellationToken _) =>
            {
                OnDelete?.Invoke();
                DeleteCutoffs.Add(cutoff);
                return count;
            });
    }

    public Mock<IPluginTraceService> Mock { get; }
    public IPluginTraceService Object => Mock.Object;
    public List<PluginTraceFilter> CountFilters { get; } = new();
    public List<DateTime> DeleteCutoffs { get; } = new();
    public Action? OnDelete { get; set; }
}

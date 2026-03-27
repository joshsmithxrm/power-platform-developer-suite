using FluentAssertions;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class DateShifterTests
{
    private static readonly DateTime BaseDate = new(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void AbsolutePreservesOriginalDate()
    {
        // AC-29: Absolute mode returns the original value unchanged
        var elapsed = TimeSpan.FromDays(30);

        var result = DateShifter.Shift(BaseDate, DateMode.Absolute, elapsed);

        result.Should().Be(BaseDate);
    }

    [Fact]
    public void RelativeShiftsByWeeks()
    {
        // AC-30: Relative mode rounds elapsed to nearest 7-day period
        // 10 days rounds to 1 week (7 days)
        var elapsed = TimeSpan.FromDays(10);

        var result = DateShifter.Shift(BaseDate, DateMode.Relative, elapsed);

        result.Should().Be(BaseDate.AddDays(7));
    }

    [Fact]
    public void RelativeDailyShiftsByDays()
    {
        // AC-31: RelativeDaily mode rounds elapsed to nearest whole day
        // 2.5 days rounds to 2 days (Math.Round with banker's rounding: 2.5 -> 2)
        var elapsed = TimeSpan.FromDays(2.5);

        var result = DateShifter.Shift(BaseDate, DateMode.RelativeDaily, elapsed);

        result.Should().Be(BaseDate.AddDays(2));
    }

    [Fact]
    public void RelativeExactShiftsByExactTime()
    {
        // AC-32: RelativeExact mode shifts by the exact elapsed time
        var elapsed = TimeSpan.FromHours(36.5);

        var result = DateShifter.Shift(BaseDate, DateMode.RelativeExact, elapsed);

        result.Should().Be(BaseDate.Add(elapsed));
    }

    [Fact]
    public void NullValueReturnsNull()
    {
        var result = DateShifter.Shift(null, DateMode.Relative, TimeSpan.FromDays(10));

        result.Should().BeNull();
    }
}

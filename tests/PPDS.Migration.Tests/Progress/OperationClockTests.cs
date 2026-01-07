using FluentAssertions;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class OperationClockTests
{
    [Fact]
    public void Elapsed_BeforeStart_ReturnsZeroOrSmallValue()
    {
        // Note: OperationClock is static, so if Start() was called in another test,
        // Elapsed won't be zero. We just verify it returns a TimeSpan without throwing.
        var elapsed = OperationClock.Elapsed;

        Assert.True(elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void Start_ResetsElapsedTime()
    {
        // Start the clock
        OperationClock.Start();
        var elapsed1 = OperationClock.Elapsed;

        // Wait a bit
        Thread.Sleep(50);
        var elapsed2 = OperationClock.Elapsed;

        // Elapsed should have increased
        elapsed2.Should().BeGreaterThan(elapsed1);

        // Restart the clock
        OperationClock.Start();
        var elapsed3 = OperationClock.Elapsed;

        // Elapsed should be reset to near zero (less than what it was before restart)
        elapsed3.Should().BeLessThan(elapsed2);
    }

    [Fact]
    public void Elapsed_IncrementsOverTime()
    {
        OperationClock.Start();
        var elapsed1 = OperationClock.Elapsed;

        Thread.Sleep(100);
        var elapsed2 = OperationClock.Elapsed;

        elapsed2.Should().BeGreaterThan(elapsed1);
        Assert.True((elapsed2 - elapsed1) >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Elapsed_IsThreadSafe()
    {
        OperationClock.Start();

        var exceptions = new List<Exception>();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    var elapsed = OperationClock.Elapsed;
                    Assert.True(elapsed >= TimeSpan.Zero);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("concurrent access to Elapsed should not throw");
    }
}

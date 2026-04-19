using System;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Diagnostics;
using Xunit;

namespace PPDS.Dataverse.Tests.Diagnostics;

[Trait("Category", "Unit")]
public class CorrelationIdScopeTests
{
    [Fact]
    public void Current_WhenNoScope_ReturnsNull()
    {
        // Reset to ensure prior test runs don't leak.
        using (CorrelationIdScope.Push(null))
        {
            Assert.Null(CorrelationIdScope.Current);
        }
    }

    [Fact]
    public void Push_SetsCurrentAndRestoresOnDispose()
    {
        using (CorrelationIdScope.Push(null))
        {
            Assert.Null(CorrelationIdScope.Current);

            using (CorrelationIdScope.Push("abc-123"))
            {
                Assert.Equal("abc-123", CorrelationIdScope.Current);
            }

            Assert.Null(CorrelationIdScope.Current);
        }
    }

    [Fact]
    public void Push_NestedScopes_RestorePreviousValue()
    {
        using (CorrelationIdScope.Push("outer"))
        {
            Assert.Equal("outer", CorrelationIdScope.Current);

            using (CorrelationIdScope.Push("inner"))
            {
                Assert.Equal("inner", CorrelationIdScope.Current);
            }

            Assert.Equal("outer", CorrelationIdScope.Current);
        }
    }

    [Fact]
    public void Push_NullOrWhitespace_ClearsCurrent()
    {
        using (CorrelationIdScope.Push("outer"))
        {
            using (CorrelationIdScope.Push("   "))
            {
                Assert.Null(CorrelationIdScope.Current);
            }

            Assert.Equal("outer", CorrelationIdScope.Current);
        }
    }

    [Fact]
    public async Task Current_FlowsAcrossAwaitPoints()
    {
        using (CorrelationIdScope.Push("flow-test"))
        {
            await Task.Yield();
            Assert.Equal("flow-test", CorrelationIdScope.Current);

            // Task continuations must see the same value.
            await Task.Run(() =>
            {
                Assert.Equal("flow-test", CorrelationIdScope.Current);
            });
        }
    }

    [Fact]
    public async Task Current_IsIsolatedPerAsyncFlow()
    {
        // Two concurrent flows each push their own id. Neither should see the other's value.
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        string? leftObserved = null;
        string? rightObserved = null;

        var left = Task.Run(() =>
        {
            using (CorrelationIdScope.Push("left-id"))
            {
                ready.Signal();
                release.Wait();
                leftObserved = CorrelationIdScope.Current;
            }
        });

        var right = Task.Run(() =>
        {
            using (CorrelationIdScope.Push("right-id"))
            {
                ready.Signal();
                release.Wait();
                rightObserved = CorrelationIdScope.Current;
            }
        });

        ready.Wait();
        release.Set();
        await Task.WhenAll(left, right);

        Assert.Equal("left-id", leftObserved);
        Assert.Equal("right-id", rightObserved);
    }

    [Fact]
    public void NewId_GeneratesValidGuidFormat()
    {
        var id = CorrelationIdScope.NewId();
        Assert.True(Guid.TryParse(id, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }
}

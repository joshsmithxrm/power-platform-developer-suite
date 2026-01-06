using FluentAssertions;
using Moq;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Dataverse.Tests.BulkOperations;

public class BatchParallelismCoordinatorTests : IDisposable
{
    private readonly Mock<IDataverseConnectionPool> _mockPool;
    private BatchParallelismCoordinator? _coordinator;

    public BatchParallelismCoordinatorTests()
    {
        _mockPool = new Mock<IDataverseConnectionPool>();
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(10);
    }

    public void Dispose()
    {
        _coordinator?.Dispose();
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithValidPool_InitializesWithPoolDop()
    {
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(8);

        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        _coordinator.CurrentCapacity.Should().Be(8);
        _coordinator.AvailableSlots.Should().Be(8);
    }

    [Fact]
    public void Constructor_WithZeroDop_InitializesWithMinimumOfOne()
    {
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(0);

        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        _coordinator.CurrentCapacity.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Constructor_WithNullPool_ThrowsArgumentNullException()
    {
        Action act = () => new BatchParallelismCoordinator(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("pool");
    }

    [Fact]
    public void Constructor_WithCustomTimeout_AcceptsTimeout()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object, TimeSpan.FromSeconds(30));

        // Coordinator should be created without error
        _coordinator.CurrentCapacity.Should().Be(10);
    }

    #endregion

    #region AcquireAsync Tests

    [Fact]
    public async Task AcquireAsync_WhenSlotsAvailable_ReturnsSlot()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);
        var initialSlots = _coordinator.AvailableSlots;

        var slot = await _coordinator.AcquireAsync();

        slot.Should().NotBeNull();
        _coordinator.AvailableSlots.Should().Be(initialSlots - 1);
    }

    [Fact]
    public async Task AcquireAsync_MultipleAcquires_ReducesAvailableSlots()
    {
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(5);
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        var slots = new List<IAsyncDisposable>();
        for (int i = 0; i < 3; i++)
        {
            slots.Add(await _coordinator.AcquireAsync());
        }

        _coordinator.AvailableSlots.Should().Be(2);

        foreach (var slot in slots)
            await slot.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);
        _coordinator.Dispose();

        Func<Task> act = async () => await _coordinator.AcquireAsync();

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await _coordinator.AcquireAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AcquireAsync_WhenTimeoutExceeded_ThrowsBatchCoordinatorExhaustedException()
    {
        // Create coordinator with very short timeout
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(1);
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object, TimeSpan.FromMilliseconds(50));

        // Acquire the only slot
        var slot = await _coordinator.AcquireAsync();

        // Try to acquire another - should timeout
        Func<Task> act = async () => await _coordinator.AcquireAsync();

        await act.Should().ThrowAsync<BatchCoordinatorExhaustedException>();

        await slot.DisposeAsync();
    }

    #endregion

    #region Slot Release Tests

    [Fact]
    public async Task SlotDispose_ReleasesSlotBackToPool()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);
        var initialSlots = _coordinator.AvailableSlots;

        var slot = await _coordinator.AcquireAsync();
        _coordinator.AvailableSlots.Should().Be(initialSlots - 1);

        await slot.DisposeAsync();

        _coordinator.AvailableSlots.Should().Be(initialSlots);
    }

    [Fact]
    public async Task SlotDispose_CanBeCalledMultipleTimes_Safely()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);
        var initialSlots = _coordinator.AvailableSlots;

        var slot = await _coordinator.AcquireAsync();

        // Double dispose should not cause issues
        await slot.DisposeAsync();
        await slot.DisposeAsync();

        _coordinator.AvailableSlots.Should().Be(initialSlots);
    }

    #endregion

    #region Capacity Expansion Tests

    [Fact]
    public async Task AcquireAsync_WhenPoolDopIncreases_ExpandsCapacity()
    {
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(5);
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        _coordinator.CurrentCapacity.Should().Be(5);

        // Simulate throttle recovery - DOP increases
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(10);

        // Acquiring should trigger capacity check and expand
        var slot = await _coordinator.AcquireAsync();
        await slot.DisposeAsync();

        _coordinator.CurrentCapacity.Should().Be(10);
    }

    [Fact]
    public async Task AcquireAsync_WhenPoolDopDecreases_DoesNotShrinkCapacity()
    {
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(10);
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        _coordinator.CurrentCapacity.Should().Be(10);

        // Simulate throttle - DOP decreases
        _mockPool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(5);

        // Capacity should NOT shrink (SemaphoreSlim limitation)
        var slot = await _coordinator.AcquireAsync();
        await slot.DisposeAsync();

        _coordinator.CurrentCapacity.Should().Be(10);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_Safely()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        _coordinator.Dispose();
        _coordinator.Dispose();

        // Should not throw
    }

    [Fact]
    public async Task Dispose_WithActiveSlots_DoesNotDeadlock()
    {
        _coordinator = new BatchParallelismCoordinator(_mockPool.Object);

        await using var slot = await _coordinator.AcquireAsync();

        // Dispose coordinator while slot is held - slot will be disposed by await using
        _coordinator.Dispose();

        // await using ensures slot.DisposeAsync() is called even if Dispose throws
    }

    #endregion
}

public class BatchCoordinatorExhaustedExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var ex = new BatchCoordinatorExhaustedException(2, 10, TimeSpan.FromSeconds(30));

        ex.AvailableSlots.Should().Be(2);
        ex.TotalCapacity.Should().Be(10);
        ex.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Message_ContainsRelevantInformation()
    {
        var ex = new BatchCoordinatorExhaustedException(0, 50, TimeSpan.FromSeconds(120));

        ex.Message.Should().Contain("0");
        ex.Message.Should().Contain("50");
        ex.Message.Should().Contain("120");
    }
}

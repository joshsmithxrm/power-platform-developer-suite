using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Progress;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class BulkOperationProberTests
{
    private readonly Mock<IBulkOperationExecutor> _bulkExecutor;
    private readonly BulkOperationProber _sut;

    public BulkOperationProberTests()
    {
        _bulkExecutor = new Mock<IBulkOperationExecutor>();
        _sut = new BulkOperationProber(_bulkExecutor.Object);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenBulkExecutorIsNull()
    {
        var act = () => new BulkOperationProber(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("bulkExecutor");
    }

    #region ExecuteWithProbeAsync

    [Fact]
    public async Task ExecuteWithProbeAsync_ReturnsEmptyResult_WhenRecordsIsNull()
    {
        // Act
        var result = await _sut.ExecuteWithProbeAsync(
            "account",
            null!,
            BulkOperationType.Upsert,
            new BulkOperationOptions(),
            (_, _) => Task.FromResult(new BulkOperationResult()),
            null,
            CancellationToken.None);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteWithProbeAsync_ReturnsEmptyResult_WhenRecordsIsEmpty()
    {
        // Act
        var result = await _sut.ExecuteWithProbeAsync(
            "account",
            Array.Empty<Entity>(),
            BulkOperationType.Upsert,
            new BulkOperationOptions(),
            (_, _) => Task.FromResult(new BulkOperationResult()),
            null,
            CancellationToken.None);

        // Assert
        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteWithProbeAsync_UsesBulk_WhenFirstRecordSucceeds()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid() },
            new Entity("account") { Id = Guid.NewGuid() },
            new Entity("account") { Id = Guid.NewGuid() }
        };

        var probeResult = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        var remainingResult = new BulkOperationResult
        {
            SuccessCount = 2,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        // First call (probe - 1 record), second call (remaining - 2 records)
        _bulkExecutor.SetupSequence(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult)
            .ReturnsAsync(remainingResult);

        var fallbackCalled = false;

        // Act
        var result = await _sut.ExecuteWithProbeAsync(
            "account",
            records,
            BulkOperationType.Upsert,
            new BulkOperationOptions(),
            (_, _) =>
            {
                fallbackCalled = true;
                return Task.FromResult(new BulkOperationResult());
            },
            null,
            CancellationToken.None);

        // Assert
        result.SuccessCount.Should().Be(3);
        result.FailureCount.Should().Be(0);
        fallbackCalled.Should().BeFalse("bulk was supported, fallback should not be invoked");
    }

    [Fact]
    public async Task ExecuteWithProbeAsync_FallsBackToIndividual_WhenBulkFails()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("team") { Id = Guid.NewGuid() },
            new Entity("team") { Id = Guid.NewGuid() }
        };

        // Probe fails with "not supported" error
        var probeResult = new BulkOperationResult
        {
            SuccessCount = 0,
            FailureCount = 1,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError
                {
                    Index = 0,
                    ErrorCode = -1,
                    Message = "UpsertMultiple is not enabled on the entity team"
                }
            }
        };

        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "team",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult);

        var fallbackResult = new BulkOperationResult
        {
            SuccessCount = 2,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        // Act
        var result = await _sut.ExecuteWithProbeAsync(
            "team",
            records,
            BulkOperationType.Upsert,
            new BulkOperationOptions(),
            (_, recs) =>
            {
                // Fallback receives ALL records (including the probe record)
                recs.Count.Should().Be(2);
                return Task.FromResult(fallbackResult);
            },
            null,
            CancellationToken.None);

        // Assert
        result.SuccessCount.Should().Be(2);
        result.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteWithProbeAsync_UsesDirectFallback_WhenEntityAlreadyKnownUnsupported()
    {
        // Arrange
        _sut.MarkBulkNotSupported("team");

        var records = new List<Entity>
        {
            new Entity("team") { Id = Guid.NewGuid() }
        };

        var fallbackResult = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        var fallbackCalled = false;

        // Act
        var result = await _sut.ExecuteWithProbeAsync(
            "team",
            records,
            BulkOperationType.Upsert,
            new BulkOperationOptions(),
            (_, _) =>
            {
                fallbackCalled = true;
                return Task.FromResult(fallbackResult);
            },
            null,
            CancellationToken.None);

        // Assert
        result.SuccessCount.Should().Be(1);
        fallbackCalled.Should().BeTrue("entity is known unsupported, should use fallback directly");
        _bulkExecutor.Verify(x => x.UpsertMultipleAsync(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<Entity>>(),
            It.IsAny<BulkOperationOptions>(),
            It.IsAny<IProgress<ProgressSnapshot>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteWithProbeAsync_ReturnsSingleRecord_WhenOnlyOneRecordAndProbeSucceeds()
    {
        // Arrange
        var records = new List<Entity>
        {
            new Entity("account") { Id = Guid.NewGuid() }
        };

        var probeResult = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        _bulkExecutor.Setup(x => x.UpsertMultipleAsync(
                "account",
                It.IsAny<IEnumerable<Entity>>(),
                It.IsAny<BulkOperationOptions>(),
                It.IsAny<IProgress<ProgressSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(probeResult);

        // Act
        var result = await _sut.ExecuteWithProbeAsync(
            "account",
            records,
            BulkOperationType.Upsert,
            new BulkOperationOptions(),
            (_, _) => Task.FromResult(new BulkOperationResult()),
            null,
            CancellationToken.None);

        // Assert
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(0);
    }

    #endregion

    #region IsKnownBulkNotSupported

    [Fact]
    public void IsKnownBulkNotSupported_ReturnsFalse_Initially()
    {
        // Act/Assert
        _sut.IsKnownBulkNotSupported("account").Should().BeFalse();
    }

    [Fact]
    public void IsKnownBulkNotSupported_ReturnsTrue_AfterPriorFailure()
    {
        // Arrange
        _sut.MarkBulkNotSupported("team");

        // Act/Assert
        _sut.IsKnownBulkNotSupported("team").Should().BeTrue();
    }

    [Fact]
    public void IsKnownBulkNotSupported_IsCaseInsensitive()
    {
        // Arrange
        _sut.MarkBulkNotSupported("Team");

        // Act/Assert
        _sut.IsKnownBulkNotSupported("team").Should().BeTrue();
        _sut.IsKnownBulkNotSupported("TEAM").Should().BeTrue();
    }

    #endregion

    #region IsBulkNotSupportedFailure (static)

    [Fact]
    public void IsBulkNotSupportedFailure_ReturnsTrue_WhenNotEnabledOnEntity()
    {
        // Arrange
        var result = new BulkOperationResult
        {
            SuccessCount = 0,
            FailureCount = 1,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError { Message = "CreateMultiple is not enabled on the entity team" }
            }
        };

        // Act/Assert
        BulkOperationProber.IsBulkNotSupportedFailure(result, 1).Should().BeTrue();
    }

    [Fact]
    public void IsBulkNotSupportedFailure_ReturnsTrue_WhenDoesNotSupportEntitiesOfType()
    {
        // Arrange
        var result = new BulkOperationResult
        {
            SuccessCount = 0,
            FailureCount = 1,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError { Message = "does not support entities of type queue" }
            }
        };

        // Act/Assert
        BulkOperationProber.IsBulkNotSupportedFailure(result, 1).Should().BeTrue();
    }

    [Fact]
    public void IsBulkNotSupportedFailure_ReturnsFalse_WhenPartialSuccess()
    {
        // Arrange - not all records failed
        var result = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 1,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError { Message = "is not enabled on the entity" }
            }
        };

        // Act/Assert
        BulkOperationProber.IsBulkNotSupportedFailure(result, 2).Should().BeFalse();
    }

    [Fact]
    public void IsBulkNotSupportedFailure_ReturnsFalse_WhenGenericError()
    {
        // Arrange
        var result = new BulkOperationResult
        {
            SuccessCount = 0,
            FailureCount = 1,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError { Message = "Record does not exist" }
            }
        };

        // Act/Assert
        BulkOperationProber.IsBulkNotSupportedFailure(result, 1).Should().BeFalse();
    }

    [Fact]
    public void IsBulkNotSupportedFailure_ReturnsFalse_WhenNoErrors()
    {
        // Arrange
        var result = new BulkOperationResult
        {
            SuccessCount = 0,
            FailureCount = 1,
            Errors = Array.Empty<BulkOperationError>()
        };

        // Act/Assert
        BulkOperationProber.IsBulkNotSupportedFailure(result, 1).Should().BeFalse();
    }

    #endregion

    #region MergeBulkResults (static)

    [Fact]
    public void MergeBulkResults_CombinesCounts()
    {
        // Arrange
        var probe = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 0,
            CreatedCount = 1,
            UpdatedCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        var remaining = new BulkOperationResult
        {
            SuccessCount = 5,
            FailureCount = 1,
            CreatedCount = 3,
            UpdatedCount = 2,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError { Index = 2, Message = "Error" }
            }
        };

        // Act
        var merged = BulkOperationProber.MergeBulkResults(probe, remaining);

        // Assert
        merged.SuccessCount.Should().Be(6);
        merged.FailureCount.Should().Be(1);
        merged.CreatedCount.Should().Be(4);
        merged.UpdatedCount.Should().Be(2);
    }

    [Fact]
    public void MergeBulkResults_AdjustsErrorIndices()
    {
        // Arrange
        var probe = new BulkOperationResult
        {
            SuccessCount = 1,
            FailureCount = 0,
            Errors = Array.Empty<BulkOperationError>()
        };

        var remaining = new BulkOperationResult
        {
            SuccessCount = 2,
            FailureCount = 1,
            Errors = new List<BulkOperationError>
            {
                new BulkOperationError { Index = 0, Message = "Error at index 0 in remaining" }
            }
        };

        // Act
        var merged = BulkOperationProber.MergeBulkResults(probe, remaining);

        // Assert
        merged.Errors.Should().HaveCount(1);
        merged.Errors[0].Index.Should().Be(1, "index should be offset by 1 for the probe record");
    }

    #endregion
}

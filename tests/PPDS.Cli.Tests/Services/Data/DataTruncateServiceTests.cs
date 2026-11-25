using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Progress;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// <c>data truncate</c> attempts each record at most once per run and ends: with every record deleted, or with the
/// count of deleted and failed records and the reason it stopped (#1922).
/// </summary>
[Trait("Category", "Unit")]
public class DataTruncateServiceTests
{
    /// <summary>An in-memory table: reads return the first rows in table order; deletes remove all but the undeletable.</summary>
    private sealed class FakeTable
    {
        public List<Guid> Rows { get; } = [];
        public HashSet<Guid> Undeletable { get; } = [];
        public HashSet<Guid> ReportedDeletedButKept { get; } = [];
        public Dictionary<Guid, int> DeleteAttempts { get; } = [];
        public List<int> FetchSizes { get; } = [];
        public bool NameFailures { get; set; } = true;
        public int Fetches { get; private set; }

        public FakeTable Add(int count, bool undeletable = false)
        {
            for (var i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                Rows.Add(id);
                if (undeletable) Undeletable.Add(id);
            }
            return this;
        }

        public IReadOnlyList<Guid> Fetch(int size)
        {
            // A safety net so a regression fails the test instead of hanging it.
            if (++Fetches > 10_000) throw new InvalidOperationException("truncate did not end");
            FetchSizes.Add(size);
            return Rows.Take(size).ToList();
        }

        public BulkOperationResult Delete(IEnumerable<Guid> ids)
        {
            var list = ids.ToList();
            var errors = new List<BulkOperationError>();
            var succeeded = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var id = list[i];
                DeleteAttempts[id] = DeleteAttempts.GetValueOrDefault(id) + 1;
                if (Undeletable.Contains(id))
                {
                    errors.Add(NameFailures
                        ? new BulkOperationError { Index = i, RecordId = id, ErrorCode = -1, Message = "restricted" }
                        : new BulkOperationError { Index = -1, ErrorCode = -1, Message = "restricted" });
                    continue;
                }

                if (!ReportedDeletedButKept.Contains(id)) Rows.Remove(id);
                succeeded++;
            }

            return new BulkOperationResult { SuccessCount = succeeded, FailureCount = errors.Count, Errors = errors };
        }
    }

    private static DataTruncateService CreateService(FakeTable table)
    {
        var query = new Mock<IDataQueryService>();
        query.Setup(q => q.FetchBatchIdsAsync("account", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, int size, CancellationToken _) => table.Fetch(size));
        var bulk = new Mock<IBulkOperationExecutor>();
        bulk.Setup(b => b.DeleteMultipleAsync("account", It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(),
                It.IsAny<DataverseClientOptions?>(), It.IsAny<IProgress<ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, DataverseClientOptions? _,
                IProgress<ProgressSnapshot>? _, CancellationToken _) => table.Delete(ids));
        return new DataTruncateService(query.Object, bulk.Object, NullLogger<DataTruncateService>.Instance);
    }

    private static Task<DataTruncateResult> Truncate(FakeTable table, int batchSize = 2, bool continueOnError = true)
        => CreateService(table).TruncateAsync("account",
            new DataTruncateOptions { BatchSize = batchSize, ContinueOnError = continueOnError });

    [Fact]
    public async Task OneRecordCannotBeDeleted_RunEnds_AndThatRecordIsAttemptedOnce()
    {
        var table = new FakeTable().Add(1, undeletable: true).Add(4);
        var stuck = table.Rows[0];

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(4);
        result.FailedCount.Should().Be(1);
        result.StopReason.Should().Be(DataTruncateStopReason.OnlyFailedRecordsRemain);
        result.IsSuccess.Should().BeFalse();
        table.DeleteAttempts[stuck].Should().Be(1);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
        table.Rows.Should().Equal(stuck);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.PartialSuccess);
    }

    [Fact]
    public async Task EveryRecordOfAPassFails_PassDeletedNothing_EndsTheRun()
    {
        var table = new FakeTable().Add(2, undeletable: true).Add(3);

        var result = await Truncate(table);

        result.Passes.Should().Be(1);
        result.DeletedCount.Should().Be(0);
        result.FailedCount.Should().Be(2);
        result.StopReason.Should().Be(DataTruncateStopReason.PassDeletedNothing);
        TruncateCommand.ToExitCode(result).Should().NotBe(ExitCodes.Success);
    }

    [Fact]
    public async Task FailuresTheExecutorCannotName_StillEnd_AndAreCountedOnce()
    {
        var table = new FakeTable { NameFailures = false }.Add(1, undeletable: true).Add(3);

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(3);
        result.FailedCount.Should().Be(1);
        result.StopReason.Should().Be(DataTruncateStopReason.OnlyFailedRecordsRemain);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
    }

    [Fact]
    public async Task RecordReportedDeletedIsReadAgain_RunStops_CountsNotCertain()
    {
        var table = new FakeTable().Add(4);
        table.ReportedDeletedButKept.Add(table.Rows[0]);

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.DeletedRecordReadAgain);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.Failure);
    }

    [Fact]
    public async Task StopOnErrorWithoutContinueOnError()
    {
        var table = new FakeTable().Add(1).Add(1, undeletable: true).Add(4);

        var result = await Truncate(table, continueOnError: false);

        result.Passes.Should().Be(1);
        result.StopReason.Should().Be(DataTruncateStopReason.StoppedOnError);
    }

    [Fact]
    public async Task AllRecordsDeletable_Completed_ExitZero()
    {
        var table = new FakeTable().Add(5);

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(5);
        result.FailedCount.Should().Be(0);
        result.Passes.Should().Be(3);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.IsSuccess.Should().BeTrue();
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.Success);
        table.FetchSizes.Should().OnlyContain(s => s == 2);
    }

    [Fact]
    public async Task FailedRecordsAtTheFront_DoNotCrowdOutTheNextBatch()
    {
        // Each pass: one failure then one success. The read grows by the failures so a full batch is still fresh.
        var table = new FakeTable();
        for (var i = 0; i < 3; i++) table.Add(1, undeletable: true).Add(1);

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(3);
        result.FailedCount.Should().Be(3);
        table.FetchSizes.Should().StartWith([2, 3]);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
    }

    [Fact]
    public async Task ThousandsOfFailures_ReadsStayWithinTheLimit_AndTheRunEnds()
    {
        // Nine records in ten cannot be deleted: the failed records grow until the read reaches its 5,000-row limit.
        var table = new FakeTable();
        for (var i = 0; i < 800; i++) table.Add(9, undeletable: true).Add(1);

        var result = await Truncate(table, batchSize: 1000);

        table.FetchSizes.Should().OnlyContain(s => s <= DataTruncateService.MaxFetch);
        table.FetchSizes.Should().Contain(DataTruncateService.MaxFetch);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
        result.FailedCount.Should().BeLessThanOrEqualTo(DataTruncateService.MaxFetch);
        result.DeletedCount.Should().BePositive();
        (result.DeletedCount + result.FailedCount).Should().Be(table.DeleteAttempts.Count);
        result.StopReason.Should().BeOneOf(DataTruncateStopReason.PassDeletedNothing, DataTruncateStopReason.OnlyFailedRecordsRemain);
    }

    [Fact]
    public async Task EmptyTable_Completed()
    {
        var result = await Truncate(new FakeTable());

        result.Passes.Should().Be(0);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
    }
}

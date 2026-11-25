using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// <c>data truncate</c> reads the table once, page by page after the last primary id read, attempts each record at most
/// once, and ends with the counts of deleted, failed and remaining records and the true reason it stopped (#1922).
/// </summary>
[Trait("Category", "Unit")]
public class DataTruncateServiceTests
{
    /// <summary>
    /// An in-memory table behind a fake pool: pages in primary-id order continue after the id in the paging cookie, as
    /// Dataverse's do; deletes remove every record but the undeletable ones.
    /// </summary>
    private sealed class FakeTable
    {
        public SortedSet<Guid> Rows { get; } = [];
        public HashSet<Guid> Undeletable { get; } = [];
        public HashSet<Guid> ReportedDeletedButKept { get; } = [];
        public Dictionary<Guid, int> DeleteAttempts { get; } = [];
        public List<QueryExpression> Reads { get; } = [];
        public bool NameFailures { get; set; } = true;
        public bool ReturnCookies { get; set; } = true;
        public bool RestartEveryRead { get; set; }
        public string PrimaryId { get; set; } = "accountid";
        public bool EmptyPagesClaimMore { get; set; }
        public bool FailCounts { get; set; }
        public int? ThrowOnRead { get; set; }
        public Action<int>? AfterDelete { get; set; }
        public int Deletes { get; private set; }

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

        public EntityCollection Read(QueryBase queryBase)
        {
            if (queryBase is FetchExpression)
            {
                if (FailCounts) throw new InvalidOperationException("aggregate limit exceeded");
                // The aggregate count.
                var counted = new Entity("account") { ["count"] = new AliasedValue("account", PrimaryId, Rows.Count) };
                return new EntityCollection([counted]);
            }

            var query = (QueryExpression)queryBase;
            if (Reads.Count > 10_000) throw new InvalidOperationException("truncate did not end");
            Reads.Add(query);
            if (ThrowOnRead == Reads.Count) throw new InvalidOperationException("the service is unavailable");
            if (EmptyPagesClaimMore && Reads.Count > 1)
            {
                return new EntityCollection { MoreRecords = true, PagingCookie = "cookie-" + Reads.Count };
            }
            var after = !RestartEveryRead && query.PageInfo.PagingCookie is { } cookie ? Guid.Parse(cookie) : (Guid?)null;
            var page = Rows.Where(id => after is null || id.CompareTo(after.Value) > 0).Take(query.PageInfo.Count + 1).ToList();
            var more = page.Count > query.PageInfo.Count;
            page = page.Take(query.PageInfo.Count).ToList();
            return new EntityCollection(page.Select(id => new Entity("account", id) { [PrimaryId] = id }).ToList())
            {
                MoreRecords = more,
                PagingCookie = ReturnCookies && page.Count > 0 ? page[^1].ToString() : null
            };
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

            Deletes++;
            AfterDelete?.Invoke(Deletes);
            return new BulkOperationResult { SuccessCount = succeeded, FailureCount = errors.Count, Errors = errors };
        }
    }

    private static DataTruncateService CreateService(FakeTable table)
    {
        var client = new Mock<IPooledClient>();
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase q, CancellationToken _) => table.Read(q));
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var metadata = new Mock<IMetadataQueryService>();
        metadata.Setup(m => m.GetEntityAsync("account", false, false, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadataDto
            {
                LogicalName = "account", SchemaName = "Account", DisplayName = "Account", PrimaryIdAttribute = table.PrimaryId
            });
        var bulk = new Mock<IBulkOperationExecutor>();
        bulk.Setup(b => b.DeleteMultipleAsync("account", It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(),
                It.IsAny<DataverseClientOptions?>(), It.IsAny<IProgress<ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, DataverseClientOptions? _,
                IProgress<ProgressSnapshot>? _, CancellationToken _) => table.Delete(ids));
        return new DataTruncateService(pool.Object, metadata.Object, bulk.Object, NullLogger<DataTruncateService>.Instance);
    }

    private static Task<DataTruncateResult> Truncate(FakeTable table, int batchSize = 2, bool continueOnError = true,
        CancellationToken cancellationToken = default)
        => CreateService(table).TruncateAsync("account",
            new DataTruncateOptions { BatchSize = batchSize, ContinueOnError = continueOnError },
            cancellationToken: cancellationToken);

    [Fact]
    public async Task OneRecordCannotBeDeleted_RunEnds_AndThatRecordIsAttemptedOnce()
    {
        var table = new FakeTable().Add(1, undeletable: true).Add(4);
        var stuck = table.Undeletable.Single();

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(4);
        result.FailedCount.Should().Be(1);
        result.FailedIds.Should().Equal(stuck);
        result.RemainingCount.Should().Be(1);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.IsSuccess.Should().BeFalse();
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
        table.Rows.Should().Equal(stuck);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.PartialSuccess);
        TruncateCommand.DescribeStop(result).Should().Contain("attempted once").And.Contain("were not retried");
    }

    [Fact]
    public async Task LastPageHoldsOnlyAnUndeletableRecord_ReportsCompleted()
    {
        var table = new FakeTable().Add(5);
        table.Undeletable.Add(table.Rows.Max);

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.DeletedCount.Should().Be(4);
        result.FailedCount.Should().Be(1);
        result.RemainingCount.Should().Be(1);
    }

    [Fact]
    public async Task ThousandsOfFailures_DoNotHideTheRecordsAfterThem()
    {
        // Nine records in ten cannot be deleted (7,000 records): every deletable record is still reached.
        var table = new FakeTable();
        for (var i = 0; i < 700; i++) table.Add(9, undeletable: true).Add(1);
        var deletable = table.Rows.Count - table.Undeletable.Count;

        var result = await Truncate(table, batchSize: 1000);

        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.DeletedCount.Should().Be(deletable);
        result.FailedCount.Should().Be(table.Undeletable.Count);
        result.RemainingCount.Should().Be(table.Undeletable.Count);
        table.DeleteAttempts.Should().HaveCount(7000);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
        table.Reads.Should().OnlyContain(q => q.PageInfo.Count == 1000 && q.TopCount == null);
        table.Rows.SetEquals(table.Undeletable).Should().BeTrue();
    }

    [Fact]
    public async Task BatchOfOne_FirstRecordUndeletable_TheOthersAreStillDeleted()
    {
        // The reviewer's probe: batch size 1, six records, the first undeletable.
        var table = new FakeTable().Add(6);
        table.Undeletable.Add(table.Rows.Min);

        var result = await Truncate(table, batchSize: 1);

        result.DeletedCount.Should().Be(5);
        result.FailedCount.Should().Be(1);
        result.RemainingCount.Should().Be(1);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        table.DeleteAttempts.Should().HaveCount(6);
    }

    [Fact]
    public async Task AWholePassFails_TheRecordsAfterItAreStillAttempted()
    {
        // The reviewer's probe: batch 2, ten records, the third and fourth undeletable (the whole second page).
        var table = new FakeTable().Add(10);
        table.Undeletable.UnionWith(table.Rows.Skip(2).Take(2));

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(8);
        result.FailedCount.Should().Be(2);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        table.DeleteAttempts.Should().HaveCount(10);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
    }

    [Fact]
    public async Task EmptyPageClaimingMore_StopsAsPagingUnavailable_InsteadOfSpinning()
    {
        var table = new FakeTable { EmptyPagesClaimMore = true }.Add(6);

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.PagingUnavailable);
        table.Reads.Should().HaveCount(2);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.PartialSuccess);
    }

    [Fact]
    public async Task RecordCreatedBehindTheReadDuringTheRun_IsLeft_AndTheRunIsNotASuccess()
    {
        var table = new FakeTable().Add(4);
        table.AfterDelete = n => { if (n == 1) table.Rows.Add(Guid.Empty); }; // sorts before every other id

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.FailedCount.Should().Be(0);
        result.RemainingCount.Should().Be(1);
        result.IsSuccess.Should().BeFalse();
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.PartialSuccess);
        TruncateCommand.DescribeStop(result).Should().Contain("records remain");
    }

    [Fact]
    public async Task RecordsCreatedAheadOfTheReadDuringTheRun_AreDeleted_AndTheNoteSaysMoreThanConfirmed()
    {
        var table = new FakeTable().Add(4);
        table.AfterDelete = n => { if (n == 1) table.Rows.Add(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")); };

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(5);
        result.IsSuccess.Should().BeTrue();
        TruncateCommand.DescribeMoreThanConfirmed(result, confirmedCount: 4).Should().Contain("5 records were deleted, more than the 4 confirmed");
        TruncateCommand.DescribeMoreThanConfirmed(result, confirmedCount: 5).Should().BeNull();
    }

    [Fact]
    public async Task RemainingCountThatCannotBeRead_IsNotZero_AndTheRunIsNotASuccess()
    {
        var table = new FakeTable { FailCounts = true }.Add(3);

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.RemainingCount.Should().BeNull();
        result.IsSuccess.Should().BeFalse();
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.PartialSuccess);
    }

    [Fact]
    public async Task PageNumbersAdvanceWithTheCookie()
    {
        var table = new FakeTable().Add(5);

        await Truncate(table);

        table.Reads.Select(q => q.PageInfo.PageNumber).Should().Equal(1, 2, 3);
        table.Reads[0].PageInfo.PagingCookie.Should().BeNull();
        table.Reads.Skip(1).Should().OnlyContain(q => q.PageInfo.PagingCookie != null);
    }

    [Fact]
    public async Task CancelledAfterAPass_ReportsWhatWasDeleted()
    {
        using var cts = new CancellationTokenSource();
        var table = new FakeTable().Add(6);
        table.AfterDelete = n => { if (n == 1) cts.Cancel(); };

        var result = await Truncate(table, cancellationToken: cts.Token);

        result.StopReason.Should().Be(DataTruncateStopReason.Cancelled);
        result.DeletedCount.Should().Be(2);
        result.RemainingCount.Should().BeNull();
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.Failure);
    }

    [Fact]
    public async Task ReadFailsAfterAPass_ReportsWhatWasDeleted_WithTheCodeNotTheMessage()
    {
        var table = new FakeTable { ThrowOnRead = 2 }.Add(6);

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.Interrupted);
        result.DeletedCount.Should().Be(2);
        result.InterruptionCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
        result.RemainingCount.Should().Be(4);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.Failure);
        TruncateCommand.DescribeStop(result).Should().NotContain("unavailable");
    }

    [Fact]
    public async Task ReadFailsBeforeAnyPass_Throws()
    {
        var table = new FakeTable { ThrowOnRead = 1 }.Add(2);

        var act = () => Truncate(table);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
    }

    [Fact]
    public async Task FailuresTheExecutorCannotName_AreCountedOnce_AndNotRetried()
    {
        var table = new FakeTable { NameFailures = false }.Add(4);
        table.Undeletable.Add(table.Rows.First());

        var result = await Truncate(table);

        result.DeletedCount.Should().Be(3);
        result.FailedCount.Should().Be(1);
        result.FailedIds.Should().BeEmpty();
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
    }

    [Fact]
    public async Task RecordReadAgain_RunStops_CountsNotCertain()
    {
        // A read that does not move past the previous page returns a record already attempted.
        var table = new FakeTable { RestartEveryRead = true }.Add(4);
        table.ReportedDeletedButKept.Add(table.Rows.First());

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.DeletedRecordReadAgain);
        table.DeleteAttempts.Values.Should().OnlyContain(n => n == 1);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.Failure);
    }

    [Fact]
    public async Task NoPagingCookie_StopsInsteadOfPagingByPosition()
    {
        var table = new FakeTable { ReturnCookies = false }.Add(5);

        var result = await Truncate(table);

        result.StopReason.Should().Be(DataTruncateStopReason.PagingUnavailable);
        result.Passes.Should().Be(1);
        result.RemainingCount.Should().Be(3);
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.PartialSuccess);
    }

    [Fact]
    public async Task StopOnErrorWithoutContinueOnError()
    {
        var table = new FakeTable().Add(6);
        table.Undeletable.Add(table.Rows.ElementAt(1));

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
        result.RemainingCount.Should().Be(0);
        result.Passes.Should().Be(3);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.IsSuccess.Should().BeTrue();
        TruncateCommand.ToExitCode(result).Should().Be(ExitCodes.Success);
        TruncateCommand.DescribeStop(result).Should().BeNull();
        table.Reads.Should().OnlyContain(q => q.Orders.Single().AttributeName == "accountid" &&
                                              q.Orders.Single().OrderType == OrderType.Ascending);
    }

    [Fact]
    public async Task PrimaryIdComesFromMetadata()
    {
        var table = new FakeTable { PrimaryId = "activityid" }.Add(3);

        var result = await Truncate(table);

        result.IsSuccess.Should().BeTrue();
        table.Reads.Should().OnlyContain(q => q.ColumnSet.Columns.Single() == "activityid" &&
                                              q.Orders.Single().AttributeName == "activityid");
    }

    [Fact]
    public async Task EmptyTable_Completed()
    {
        var result = await Truncate(new FakeTable());

        result.Passes.Should().Be(0);
        result.StopReason.Should().Be(DataTruncateStopReason.Completed);
        result.IsSuccess.Should().BeTrue();
    }
}

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.PluginTraces;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.PluginTraces;

/// <summary>
/// Issue 1920: deleting plug-in traces by age. The age is parsed in one exact form and must be
/// positive; the cut-off is computed once, in UTC, and compared with <c>lt</c> (not the
/// date-worded <c>on-or-before</c>), so a clean-up cannot delete more than was asked.
/// </summary>
[Trait("Category", "Unit")]
public class PluginTraceAgeDeleteTests
{
    // ─── The one accepted form ──────────────────────────────────────────────────

    [Theory]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("1d", 24 * 60)]
    [InlineData("24h", 24 * 60)]
    [InlineData("30m", 30)]
    [InlineData("1m", 1)]
    [InlineData("36500d", 36500 * 24 * 60)]
    public void Parse_AcceptsTheDocumentedForm(string text, int minutes)
    {
        PluginTraceAge.Parse(text).Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0d")]
    [InlineData("0m")]
    [InlineData("-7d")]
    [InlineData("-1")]
    [InlineData("-0d")]
    [InlineData("+7d")]
    [InlineData("7")]          // no unit: no longer read as days
    [InlineData("07d")]
    [InlineData("7D")]
    [InlineData("7 d")]
    [InlineData(" 7d")]
    [InlineData("7d ")]
    [InlineData("7d\n")]
    [InlineData("7dx")]
    [InlineData("7days")]
    [InlineData("1.5d")]
    [InlineData("1e3m")]
    [InlineData("7w")]
    [InlineData("7s")]
    [InlineData("d")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("36501d")]     // more than 100 years
    [InlineData("876001h")]
    [InlineData("99999999m")]
    [InlineData("٧d")]         // a digit that is not ASCII
    public void Parse_RefusesAnythingElse(string? text)
    {
        var act = () => PluginTraceAge.Parse(text);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    [Fact]
    public void Parse_ErrorDoesNotRepeatTheInput()
    {
        var ex = Assert.Throws<PpdsException>(() => PluginTraceAge.Parse("7dSECRET"));

        ex.UserMessage.Should().NotContain("SECRET");
        ex.UserMessage.Should().Contain("7d, 24h or 30m");
    }

    [Fact]
    public void CutoffUtc_IsNowLessTheAge()
    {
        var now = new DateTime(2026, 11, 25, 5, 38, 27, DateTimeKind.Utc);

        var cutoff = PluginTraceAge.CutoffUtc(TimeSpan.FromMinutes(30), now);

        cutoff.Should().Be(new DateTime(2026, 11, 25, 5, 8, 27, DateTimeKind.Utc));
        cutoff.Kind.Should().Be(DateTimeKind.Utc);
        PluginTraceAge.Format(cutoff).Should().Be("2026-11-25T05:08:27Z");
    }

    [Fact]
    public void CutoffUtc_RefusesALocalClock()
    {
        var act = () => PluginTraceAge.CutoffUtc(TimeSpan.FromDays(1), DateTime.Now);

        act.Should().Throw<ArgumentException>();
    }

    // ─── The service refuses before anything is read ────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-7 * 24 * 60)]
    public async Task DeleteOlderThanAsync_RefusesZeroAndNegativeAges_BeforeReadingAnything(int minutes)
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();

        var act = () => service.DeleteOlderThanAsync(TimeSpan.FromMinutes(minutes));

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        store.ClientsTaken.Should().Be(0);
        store.Deleted.Should().BeEmpty();
        store.Remaining.Should().Be(10);
    }

    [Fact]
    public async Task DeleteCreatedBeforeAsync_RefusesACutoffInTheFuture_BeforeReadingAnything()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();

        var act = () => service.DeleteCreatedBeforeAsync(DateTime.UtcNow.AddDays(7));

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        store.ClientsTaken.Should().Be(0);
        store.Remaining.Should().Be(10);
    }

    [Fact]
    public async Task DeleteCreatedBeforeAsync_RefusesACutoffThatIsNotUtc()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();
        var unspecified = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-1), DateTimeKind.Unspecified);

        var act = () => service.DeleteCreatedBeforeAsync(unspecified);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
        store.ClientsTaken.Should().Be(0);
    }

    // ─── The condition sent ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteCreatedBeforeAsync_SendsLessThanOnTheExactCutoff()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();
        var cutoff = DateTime.UtcNow.AddHours(-2);

        await service.DeleteCreatedBeforeAsync(cutoff);

        store.CreatedOnConditions.Should().NotBeEmpty();
        store.CreatedOnConditions.Should().OnlyContain(c => c.Operator == ConditionOperator.LessThan);
        store.CreatedOnConditions.Should().OnlyContain(c => (DateTime)c.Values[0] == cutoff);
    }

    [Fact]
    public async Task CountAsync_SendsLtAndGeOnTheFullTimestamp()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();
        var before = new DateTime(2026, 11, 25, 5, 8, 27, DateTimeKind.Utc);
        var after = new DateTime(2026, 11, 20, 1, 2, 3, DateTimeKind.Utc);

        await service.CountAsync(new PluginTraceFilter { CreatedBefore = before, CreatedAfter = after });

        var conditions = store.FetchConditions.Where(c => c.Attribute == "createdon").ToList();
        conditions.Should().HaveCount(2);
        conditions.Should().ContainSingle(c => c.Operator == "lt" && c.Value == "2026-11-25T05:08:27.0000000Z");
        conditions.Should().ContainSingle(c => c.Operator == "ge" && c.Value == "2026-11-20T01:02:03.0000000Z");
    }

    [Fact]
    public async Task ListAsync_SendsGreaterEqualForCreatedAfter()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();
        var after = DateTime.UtcNow.AddHours(-1);

        await service.ListAsync(new PluginTraceFilter { CreatedAfter = after });

        store.CreatedOnConditions.Should().ContainSingle()
            .Which.Operator.Should().Be(ConditionOperator.GreaterEqual);
    }

    // ─── What gets deleted (the issue's two examples) ───────────────────────────

    [Fact]
    public async Task DeleteOlderThanAsync_ThirtyMinutes_DeletesOnlyTheTracesOlderThanThirtyMinutes()
    {
        // Traces aged 10 minutes, 20 minutes, 3 days and 4 days. The fake compares `on-or-before`
        // by date only, as its documented wording ("on or before a specified date") allows; `lt`
        // compares the full value.
        var now = DateTime.UtcNow;
        var store = new TraceStore(new[]
        {
            now.AddMinutes(-10), now.AddMinutes(-20), now.AddDays(-3), now.AddDays(-4),
        });
        var service = store.CreateService();

        var deleted = await service.DeleteOlderThanAsync(TimeSpan.FromMinutes(30));

        deleted.Should().Be(2);
        store.Deleted.Should().HaveCount(2);
        store.Remaining.Should().Be(2);
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NegativeSevenDays_DeletesNothing()
    {
        var store = new TraceStore(TenTracesFromTheLastHours());
        var service = store.CreateService();

        var act = () => service.DeleteOlderThanAsync(TimeSpan.FromDays(-7));

        await act.Should().ThrowAsync<PpdsException>();
        store.Deleted.Should().BeEmpty();
        store.Remaining.Should().Be(10);
    }

    // ─── The command's confirmation rule ────────────────────────────────────────

    [Theory]
    [InlineData(false, false, true, false)]   // JSON output, no --force
    [InlineData(false, false, false, true)]   // redirected input, no --force
    [InlineData(false, false, true, true)]
    public void OlderThan_WithoutAWayToConfirm_IsRefused(bool force, bool dryRun, bool json, bool redirected)
    {
        DeleteCommand.OlderThanConfirmationProblem(force, dryRun, json, redirected)
            .Should().NotBeNull().And.Contain("--force");
    }

    [Theory]
    [InlineData(true, false, true, true)]     // --force
    [InlineData(false, true, true, true)]     // --dry-run deletes nothing
    [InlineData(false, false, false, false)]  // a terminal: the prompt shows the cut-off and count
    public void OlderThan_WithForceDryRunOrATerminal_GoesAhead(bool force, bool dryRun, bool json, bool redirected)
    {
        DeleteCommand.OlderThanConfirmationProblem(force, dryRun, json, redirected).Should().BeNull();
    }

    // ─── A recording fake of the trace table ────────────────────────────────────

    private static DateTime[] TenTracesFromTheLastHours()
    {
        var now = DateTime.UtcNow;
        return Enumerable.Range(0, 10).Select(i => now.AddMinutes(-30 * i)).ToArray();
    }

    private sealed record FetchCondition(string Attribute, string Operator, string Value);

    private sealed class TraceStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, DateTime> _rows;

        public TraceStore(IEnumerable<DateTime> createdOn)
        {
            _rows = createdOn.ToDictionary(_ => Guid.NewGuid(), c => c);
        }

        public int ClientsTaken;
        public List<Guid> Deleted { get; } = new();
        public List<ConditionExpression> CreatedOnConditions { get; } = new();
        public List<FetchCondition> FetchConditions { get; } = new();

        public int Remaining
        {
            get { lock (_gate) return _rows.Count; }
        }

        public PluginTraceService CreateService()
        {
            var client = new Mock<IPooledClient>(MockBehavior.Loose);
            client
                .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((QueryBase q, CancellationToken _) => Retrieve(q));
            client
                .Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((string _, Guid id, CancellationToken _) =>
                {
                    lock (_gate)
                    {
                        if (_rows.Remove(id)) Deleted.Add(id);
                    }
                    return Task.CompletedTask;
                });

            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
            pool
                .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Interlocked.Increment(ref ClientsTaken);
                    return client.Object;
                });
            pool.Setup(p => p.GetTotalRecommendedParallelism()).Returns(4);

            return new PluginTraceService(pool.Object, new InactiveFakeShakedownGuard(), NullLogger<PluginTraceService>.Instance);
        }

        private EntityCollection Retrieve(QueryBase query)
        {
            lock (_gate)
            {
                if (query is FetchExpression fetch)
                {
                    var conditions = XDocument.Parse(fetch.Query).Descendants("condition")
                        .Select(c => new FetchCondition(
                            (string?)c.Attribute("attribute") ?? "",
                            (string?)c.Attribute("operator") ?? "",
                            (string?)c.Attribute("value") ?? ""))
                        .ToList();
                    FetchConditions.AddRange(conditions);
                    var count = _rows.Values.Count(v => conditions
                        .Where(c => c.Attribute == "createdon")
                        .All(c => Matches(v, c.Operator, DateTime.Parse(c.Value, null,
                            System.Globalization.DateTimeStyles.RoundtripKind))));
                    var row = new Entity("plugintracelog");
                    row["count"] = new AliasedValue("plugintracelog", "plugintracelogid", count);
                    return new EntityCollection(new List<Entity> { row });
                }

                var expression = (QueryExpression)query;
                var dateConditions = expression.Criteria.Conditions
                    .Where(c => c.AttributeName == "createdon").ToList();
                CreatedOnConditions.AddRange(dateConditions);
                var top = expression.TopCount ?? int.MaxValue;
                var matches = _rows
                    .Where(r => dateConditions.All(c => Matches(r.Value, c.Operator, (DateTime)c.Values[0])))
                    .Take(top)
                    .Select(r => new Entity("plugintracelog", r.Key) { ["createdon"] = r.Value })
                    .ToList();
                return new EntityCollection(matches);
            }
        }

        // `on-or-before` / `on-or-after` are compared by date only (the reading their documented
        // wording allows); `lt` / `ge` compare the full value.
        private static bool Matches(DateTime value, ConditionOperator op, DateTime compared) => op switch
        {
            ConditionOperator.LessThan => value < compared,
            ConditionOperator.GreaterEqual => value >= compared,
            ConditionOperator.OnOrBefore => value.Date <= compared.Date,
            ConditionOperator.OnOrAfter => value.Date >= compared.Date,
            _ => throw new NotSupportedException(op.ToString()),
        };

        private static bool Matches(DateTime value, string op, DateTime compared) => op switch
        {
            "lt" => Matches(value, ConditionOperator.LessThan, compared),
            "ge" => Matches(value, ConditionOperator.GreaterEqual, compared),
            "on-or-before" => Matches(value, ConditionOperator.OnOrBefore, compared),
            "on-or-after" => Matches(value, ConditionOperator.OnOrAfter, compared),
            _ => throw new NotSupportedException(op),
        };
    }
}

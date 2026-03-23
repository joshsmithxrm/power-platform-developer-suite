using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class ConsoleProgressReporterTests : IDisposable
{
    private readonly TextWriter _originalStdErr;
    private readonly StringWriter _capturedStdErr;

    public ConsoleProgressReporterTests()
    {
        _originalStdErr = Console.Error;
        _capturedStdErr = new StringWriter();
        Console.SetError(_capturedStdErr);
    }

    public void Dispose()
    {
        Console.SetError(_originalStdErr);
        _capturedStdErr.Dispose();
    }

    private string GetOutput() => _capturedStdErr.ToString();

    #region Complete() — Source vs Imported Comparison (#280)

    [Fact]
    public void Complete_ShowsSourceVsImported_WhenFailuresExist()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SourceRecordCount = 1000,
            SuccessCount = 950,
            FailureCount = 50,
            RecordsProcessed = 1000,
            Duration = TimeSpan.FromSeconds(10)
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("Source: 1,000");
        output.Should().Contain("Imported: 950");
        output.Should().Contain("Failed: 50");
    }

    [Fact]
    public void Complete_ShowsSourceVsImported_ExcludingM2MFailures()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = false,
            SourceRecordCount = 1000,
            SuccessCount = 970,
            FailureCount = 70, // 50 entity failures + 20 M2M failures
            RelationshipsFailed = 20,
            RecordsProcessed = 1040,
            Duration = TimeSpan.FromSeconds(10)
        };

        reporter.Complete(result);

        var output = GetOutput();
        // Should show entity-only stats: 1000 source - 50 entity failures = 950 imported
        output.Should().Contain("Source: 1,000");
        output.Should().Contain("Imported: 950");
        output.Should().Contain("Failed: 50");
    }

    [Fact]
    public void Complete_OmitsSourceComparison_WhenNoFailures()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SourceRecordCount = 100,
            SuccessCount = 100,
            FailureCount = 0,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(5)
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().NotContain("Source:");
    }

    [Fact]
    public void Complete_OmitsSourceComparison_WhenSourceCountIsNull()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SourceRecordCount = null,
            SuccessCount = 100,
            FailureCount = 0,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(5)
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().NotContain("Source:");
    }

    #endregion

    #region Complete() — API Call Count (#279)

    [Fact]
    public void Complete_ShowsApiCallCount_WhenPoolStatisticsPresent()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SuccessCount = 100,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(10),
            PoolStatistics = new PoolStatistics
            {
                RequestsServed = 1234,
                ThrottleEvents = 0,
                TotalBackoffTime = TimeSpan.Zero
            }
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("API calls: 1,234");
        output.Should().NotContain("throttled");
    }

    [Fact]
    public void Complete_ShowsThrottleInfo_WhenThrottleEventsPresent()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SuccessCount = 100,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(10),
            PoolStatistics = new PoolStatistics
            {
                RequestsServed = 500,
                ThrottleEvents = 3,
                TotalBackoffTime = TimeSpan.FromSeconds(2.5)
            }
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("API calls: 500");
        output.Should().Contain("3 throttled");
        output.Should().Contain("2.5s backoff");
    }

    [Fact]
    public void Complete_OmitsApiCallCount_WhenPoolStatisticsNull()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SuccessCount = 100,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(5),
            PoolStatistics = null
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().NotContain("API calls");
    }

    #endregion

    #region Complete() — MISSING_PARENT Pattern (#278)

    [Fact]
    public void Complete_ShowsFieldNames_ForMissingParentPattern()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var diagnostics = new List<BatchFailureDiagnostic>
        {
            new()
            {
                RecordId = Guid.NewGuid(),
                RecordIndex = 0,
                FieldName = "parentaccountid",
                ReferencedId = Guid.NewGuid(),
                Pattern = "MISSING_PARENT"
            }
        };

        // Create enough errors with MISSING_PARENT pattern to trigger the 80% threshold
        var errors = Enumerable.Range(0, 10).Select(i => new MigrationError
        {
            EntityLogicalName = "account",
            Message = $"account With Id = {Guid.NewGuid()} Does Not Exist",
            Diagnostics = diagnostics
        }).ToList();

        var result = new MigrationResult
        {
            Success = false,
            SuccessCount = 90,
            FailureCount = 10,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(5),
            Errors = errors
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("Referenced parent record does not exist");
        output.Should().Contain("Lookup fields: parentaccountid");
    }

    [Fact]
    public void Complete_ShowsMultipleFieldNames_ForMissingParentPattern()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };

        var errors = new List<MigrationError>();
        for (var i = 0; i < 10; i++)
        {
            errors.Add(new MigrationError
            {
                EntityLogicalName = "account",
                Message = $"account With Id = {Guid.NewGuid()} Does Not Exist",
                Diagnostics = new List<BatchFailureDiagnostic>
                {
                    new()
                    {
                        FieldName = i % 2 == 0 ? "parentaccountid" : "primarycontactid",
                        ReferencedId = Guid.NewGuid(),
                        Pattern = "MISSING_PARENT"
                    }
                }
            });
        }

        var result = new MigrationResult
        {
            Success = false,
            SuccessCount = 90,
            FailureCount = 10,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(5),
            Errors = errors
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("parentaccountid");
        output.Should().Contain("primarycontactid");
    }

    [Fact]
    public void Complete_ShowsMissingParentSuggestion()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };

        var errors = Enumerable.Range(0, 10).Select(_ => new MigrationError
        {
            EntityLogicalName = "account",
            Message = $"account With Id = {Guid.NewGuid()} Does Not Exist"
        }).ToList();

        var result = new MigrationResult
        {
            Success = false,
            SuccessCount = 0,
            FailureCount = 10,
            RecordsProcessed = 10,
            Duration = TimeSpan.FromSeconds(5),
            Errors = errors
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("self-referential lookups");
    }

    #endregion

    #region Complete() — Basic Output

    [Fact]
    public void Complete_ShowsSuccessMessage_WhenSuccessful()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SuccessCount = 100,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(10)
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("Import succeeded.");
        output.Should().Contain("100 record(s)");
    }

    [Fact]
    public void Complete_ShowsErrorMessage_WhenFailed()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = false,
            SuccessCount = 90,
            FailureCount = 10,
            RecordsProcessed = 100,
            Duration = TimeSpan.FromSeconds(10)
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("Import completed with errors.");
        output.Should().Contain("10 Error(s)");
    }

    [Fact]
    public void Complete_ShowsM2MBreakdown_WhenPresent()
    {
        var reporter = new ConsoleProgressReporter { OperationName = "Import" };
        var result = new MigrationResult
        {
            Success = true,
            SuccessCount = 1100,
            RecordsProcessed = 1100,
            Duration = TimeSpan.FromSeconds(10),
            M2MCount = 100
        };

        reporter.Complete(result);

        var output = GetOutput();
        output.Should().Contain("1,000 entities + 100 M2M");
    }

    #endregion
}

using System.Text.Json;
using FluentAssertions;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class ErrorReportWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ErrorReportWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ErrorReportWriterTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    #region WriteAsync Tests

    [Fact]
    public async Task WriteAsync_CreatesJsonFile()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = CreateBasicImportResult();

        await ErrorReportWriter.WriteAsync(filePath, result, "source.zip", "https://org.crm.dynamics.com");

        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_WritesValidJson()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = CreateBasicImportResult();

        await ErrorReportWriter.WriteAsync(filePath, result, "source.zip", "https://org.crm.dynamics.com");

        var json = await File.ReadAllTextAsync(filePath);
        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task WriteAsync_IncludesSourceAndTarget()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = CreateBasicImportResult();

        await ErrorReportWriter.WriteAsync(filePath, result, "mydata.zip", "https://myorg.crm.dynamics.com");

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("mydata.zip");
        json.Should().Contain("https://myorg.crm.dynamics.com");
    }

    [Fact]
    public async Task WriteAsync_WithExecutionContext_IncludesContext()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = CreateBasicImportResult();
        var context = new ImportExecutionContext
        {
            CliVersion = "1.2.3",
            SdkVersion = "2.0.0",
            RuntimeVersion = "8.0.1",
            ImportMode = "Upsert"
        };

        await ErrorReportWriter.WriteAsync(filePath, result, "data.zip", "https://org.crm.dynamics.com", context);

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("1.2.3");
        json.Should().Contain("2.0.0");
        json.Should().Contain("Upsert");
    }

    [Fact]
    public async Task WriteAsync_WithErrors_IncludesDetailedErrors()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var recordId = Guid.NewGuid();
        var result = new ImportResult
        {
            RecordsImported = 95,
            RecordsUpdated = 0,
            Duration = TimeSpan.FromMinutes(2),
            Errors = new List<MigrationError>
            {
                new()
                {
                    EntityLogicalName = "account",
                    RecordId = recordId,
                    RecordIndex = 5,
                    ErrorCode = -2147220969,
                    Message = "Entity 'systemuser' with ID does not exist",
                    Timestamp = DateTime.UtcNow
                }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, null, null);

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("account");
        json.Should().Contain("systemuser");
        json.Should().Contain("-2147220969");
    }

    [Fact]
    public async Task WriteAsync_WithErrors_GeneratesRetryManifest()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var recordId = Guid.NewGuid();
        var result = new ImportResult
        {
            RecordsImported = 95,
            Errors = new List<MigrationError>
            {
                new()
                {
                    EntityLogicalName = "account",
                    RecordId = recordId,
                    Message = "Error"
                }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, "source.zip", null);

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("retryManifest");
        json.Should().Contain("failedRecordsByEntity");
    }

    [Fact]
    public async Task WriteAsync_CalculatesCorrectSummary()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = new ImportResult
        {
            RecordsImported = 900,
            RecordsUpdated = 50,
            Duration = TimeSpan.FromMinutes(5),
            Errors = new List<MigrationError>
            {
                new() { Message = "Error 1" },
                new() { Message = "Error 2" }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, null, null);

        var json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        var summary = doc.RootElement.GetProperty("summary");

        summary.GetProperty("totalRecords").GetInt32().Should().Be(952); // 900 + 50 + 2 errors
        summary.GetProperty("successCount").GetInt32().Should().Be(950); // 900 + 50
        summary.GetProperty("failureCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task WriteAsync_WithMissingUserError_ClassifiesPattern()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = new ImportResult
        {
            RecordsImported = 95,
            Errors = new List<MigrationError>
            {
                new()
                {
                    EntityLogicalName = "account",
                    Message = "Entity 'systemuser' With Id = abc-123 Does Not Exist"
                }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, null, null);

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("MISSING_USER");
    }

    [Fact]
    public async Task WriteAsync_WithDuplicateError_ClassifiesPattern()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = new ImportResult
        {
            RecordsImported = 95,
            Errors = new List<MigrationError>
            {
                new()
                {
                    EntityLogicalName = "account",
                    Message = "A record with this key already exists"
                }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, null, null);

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("DUPLICATE_RECORD");
    }

    [Fact]
    public async Task WriteAsync_AggregatesErrorPatterns()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = new ImportResult
        {
            RecordsImported = 90,
            Errors = new List<MigrationError>
            {
                new() { EntityLogicalName = "account", Message = "Entity 'systemuser' With Id = 1 Does Not Exist" },
                new() { EntityLogicalName = "account", Message = "Entity 'systemuser' With Id = 2 Does Not Exist" },
                new() { EntityLogicalName = "contact", Message = "A record with this key already exists" }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, null, null);

        var json = await File.ReadAllTextAsync(filePath);
        using var doc = JsonDocument.Parse(json);
        var patterns = doc.RootElement.GetProperty("summary").GetProperty("errorPatterns");

        // Should have MISSING_USER:2 and DUPLICATE_RECORD:1
        patterns.GetProperty("MISSING_USER").GetInt32().Should().Be(2);
        patterns.GetProperty("DUPLICATE_RECORD").GetInt32().Should().Be(1);
    }

    #endregion

    [Fact]
    public async Task WriteAsync_WithMissingParentError_ClassifiesPattern()
    {
        var filePath = Path.Combine(_tempDir, "report.json");
        var result = new ImportResult
        {
            RecordsImported = 95,
            Errors = new List<MigrationError>
            {
                new()
                {
                    EntityLogicalName = "account",
                    Message = "account With Id = abc-123 Does Not Exist"
                }
            }
        };

        await ErrorReportWriter.WriteAsync(filePath, result, null, null);

        var json = await File.ReadAllTextAsync(filePath);
        json.Should().Contain("MISSING_PARENT");
    }

    #region Helper Methods

    private static ImportResult CreateBasicImportResult()
    {
        return new ImportResult
        {
            Success = true,
            RecordsImported = 100,
            RecordsUpdated = 10,
            Duration = TimeSpan.FromMinutes(2),
            Errors = Array.Empty<MigrationError>()
        };
    }

    #endregion
}

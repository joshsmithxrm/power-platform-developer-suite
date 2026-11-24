using FluentAssertions;
using PPDS.Dataverse.BulkOperations;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Progress;

public class ImportErrorReportTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var before = DateTime.UtcNow;
        var report = new ImportErrorReport();
        var after = DateTime.UtcNow;

        report.Version.Should().Be("1.1");
        // Bracketed by two readings of the clock the default uses, so load cannot move it outside the range.
        report.GeneratedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        report.SourceFile.Should().BeNull();
        report.TargetEnvironment.Should().BeNull();
        report.ExecutionContext.Should().BeNull();
        report.Summary.Should().NotBeNull();
        report.EntitiesSummary.Should().NotBeNull().And.BeEmpty();
        report.Errors.Should().NotBeNull().And.BeEmpty();
        report.RetryManifest.Should().BeNull();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var generatedAt = DateTime.UtcNow;
        var summary = new ImportErrorSummary { TotalRecords = 1000 };
        var entities = new List<EntityErrorSummary> { new() { EntityLogicalName = "account" } };
        var errors = new List<DetailedError> { new() { Message = "Test error" } };
        var retryManifest = new RetryManifest { SourceFile = "data.zip" };
        var context = new ImportExecutionContext { CliVersion = "1.0.0" };

        var report = new ImportErrorReport
        {
            Version = "2.0",
            GeneratedAt = generatedAt,
            SourceFile = "input.zip",
            TargetEnvironment = "https://org.crm.dynamics.com",
            ExecutionContext = context,
            Summary = summary,
            EntitiesSummary = entities,
            Errors = errors,
            RetryManifest = retryManifest
        };

        report.Version.Should().Be("2.0");
        report.GeneratedAt.Should().Be(generatedAt);
        report.SourceFile.Should().Be("input.zip");
        report.TargetEnvironment.Should().Be("https://org.crm.dynamics.com");
        report.ExecutionContext.Should().Be(context);
        report.Summary.TotalRecords.Should().Be(1000);
        report.EntitiesSummary.Should().HaveCount(1);
        report.Errors.Should().HaveCount(1);
        report.RetryManifest.Should().Be(retryManifest);
    }
}

public class ImportExecutionContextTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var context = new ImportExecutionContext();

        context.CliVersion.Should().BeEmpty();
        context.SdkVersion.Should().BeEmpty();
        context.RuntimeVersion.Should().BeEmpty();
        context.Platform.Should().BeEmpty();
        context.ImportMode.Should().BeEmpty();
        context.StripOwnerFields.Should().BeFalse();
        context.BypassPlugins.Should().BeFalse();
        context.UserMappingProvided.Should().BeFalse();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var context = new ImportExecutionContext
        {
            CliVersion = "1.2.3",
            SdkVersion = "2.0.0",
            RuntimeVersion = "8.0.1",
            Platform = "Windows 10.0.22631",
            ImportMode = "Upsert",
            StripOwnerFields = true,
            BypassPlugins = true,
            UserMappingProvided = true
        };

        context.CliVersion.Should().Be("1.2.3");
        context.SdkVersion.Should().Be("2.0.0");
        context.RuntimeVersion.Should().Be("8.0.1");
        context.Platform.Should().Be("Windows 10.0.22631");
        context.ImportMode.Should().Be("Upsert");
        context.StripOwnerFields.Should().BeTrue();
        context.BypassPlugins.Should().BeTrue();
        context.UserMappingProvided.Should().BeTrue();
    }
}

public class ImportErrorSummaryTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var summary = new ImportErrorSummary();

        summary.TotalRecords.Should().Be(0);
        summary.SuccessCount.Should().Be(0);
        summary.FailureCount.Should().Be(0);
        summary.Duration.Should().Be(TimeSpan.Zero);
        summary.ErrorPatterns.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var patterns = new Dictionary<string, int>
        {
            ["MISSING_USER"] = 50,
            ["MISSING_REFERENCE"] = 30
        };

        var summary = new ImportErrorSummary
        {
            TotalRecords = 10000,
            SuccessCount = 9920,
            FailureCount = 80,
            Duration = TimeSpan.FromMinutes(5),
            ErrorPatterns = patterns
        };

        summary.TotalRecords.Should().Be(10000);
        summary.SuccessCount.Should().Be(9920);
        summary.FailureCount.Should().Be(80);
        summary.Duration.Should().Be(TimeSpan.FromMinutes(5));
        summary.ErrorPatterns.Should().HaveCount(2);
        summary.ErrorPatterns["MISSING_USER"].Should().Be(50);
    }
}

public class EntityErrorSummaryTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var summary = new EntityErrorSummary();

        summary.EntityLogicalName.Should().BeEmpty();
        summary.TotalRecords.Should().Be(0);
        summary.FailureCount.Should().Be(0);
        summary.TopErrors.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var topErrors = new List<string>
        {
            "Entity 'systemuser' does not exist",
            "Required field missing"
        };

        var summary = new EntityErrorSummary
        {
            EntityLogicalName = "account",
            TotalRecords = 5000,
            FailureCount = 25,
            TopErrors = topErrors
        };

        summary.EntityLogicalName.Should().Be("account");
        summary.TotalRecords.Should().Be(5000);
        summary.FailureCount.Should().Be(25);
        summary.TopErrors.Should().HaveCount(2);
    }
}

public class DetailedErrorTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var error = new DetailedError();

        error.EntityLogicalName.Should().BeEmpty();
        error.RecordId.Should().BeNull();
        error.RecordIndex.Should().BeNull();
        error.ErrorCode.Should().BeNull();
        error.Message.Should().BeEmpty();
        error.Pattern.Should().BeNull();
        error.Timestamp.Should().Be(default);
        error.Diagnostics.Should().BeNull();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var recordId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;
        var diagnostics = new List<BatchFailureDiagnostic>
        {
            new() { RecordId = recordId, Pattern = "SELF_REFERENCE" }
        };

        var error = new DetailedError
        {
            EntityLogicalName = "account",
            RecordId = recordId,
            RecordIndex = 42,
            ErrorCode = -2147220969,
            Message = "Entity 'account' with ID does not exist",
            Pattern = "MISSING_REFERENCE",
            Timestamp = timestamp,
            Diagnostics = diagnostics
        };

        error.EntityLogicalName.Should().Be("account");
        error.RecordId.Should().Be(recordId);
        error.RecordIndex.Should().Be(42);
        error.ErrorCode.Should().Be(-2147220969);
        error.Message.Should().Contain("does not exist");
        error.Pattern.Should().Be("MISSING_REFERENCE");
        error.Timestamp.Should().Be(timestamp);
        error.Diagnostics.Should().HaveCount(1);
    }
}

public class RetryManifestTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var before = DateTime.UtcNow;
        var manifest = new RetryManifest();
        var after = DateTime.UtcNow;

        manifest.Version.Should().Be("1.0");
        // Bracketed by two readings of the clock the default uses, so load cannot move it outside the range.
        manifest.GeneratedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        manifest.SourceFile.Should().BeNull();
        manifest.FailedRecordsByEntity.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var generatedAt = DateTime.UtcNow;
        var failedRecords = new Dictionary<string, List<Guid>>
        {
            ["account"] = [Guid.NewGuid(), Guid.NewGuid()],
            ["contact"] = [Guid.NewGuid()]
        };

        var manifest = new RetryManifest
        {
            Version = "1.1",
            GeneratedAt = generatedAt,
            SourceFile = "original-export.zip",
            FailedRecordsByEntity = failedRecords
        };

        manifest.Version.Should().Be("1.1");
        manifest.GeneratedAt.Should().Be(generatedAt);
        manifest.SourceFile.Should().Be("original-export.zip");
        manifest.FailedRecordsByEntity.Should().HaveCount(2);
        manifest.FailedRecordsByEntity["account"].Should().HaveCount(2);
        manifest.FailedRecordsByEntity["contact"].Should().HaveCount(1);
    }
}

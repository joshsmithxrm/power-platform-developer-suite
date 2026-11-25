using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Data;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

/// <summary>
/// <c>data truncate</c>'s output and exit code at command level, with a fake truncate service (#1922): failed ids capped
/// at 100 with the omitted count, the stop reason, the remaining and confirmed counts.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public class TruncateCommandOutputTests
{
    private readonly Mock<IDataTruncateService> _service = new();

    private Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        => Task.FromResult(new ServiceCollection()
            .AddSingleton(_service.Object)
            .AddSingleton(new ResolvedConnectionInfo { EnvironmentUrl = "https://test.invalid", Profile = new AuthProfile() })
            .BuildServiceProvider());

    private async Task<(int Exit, JsonElement Output)> Run(int confirmedCount, DataTruncateResult result)
    {
        _service.Setup(s => s.CountRecordsAsync("account", It.IsAny<CancellationToken>())).ReturnsAsync(confirmedCount);
        _service.Setup(s => s.TruncateAsync("account", It.IsAny<DataTruncateOptions>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var parsed = TruncateCommand.Create(Provider).Parse(["--entity", "account", "--force", "--output-format", "json"]);
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, JsonDocument.Parse(stdout.ToString()).RootElement.Clone());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task FailedIds_AreCappedAt100_WithTheOmittedCount_AndTheStopReason()
    {
        var failed = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();
        var result = new DataTruncateResult
        {
            DeletedCount = 850,
            FailedCount = 153, // three failures the executor could not name
            FailedIds = failed,
            RemainingCount = 153,
            Passes = 1,
            StopReason = DataTruncateStopReason.Completed
        };

        var (exit, output) = await Run(1003, result);

        Assert.Equal(ExitCodes.PartialSuccess, exit);
        Assert.False(output.GetProperty("success").GetBoolean());
        Assert.Equal(failed.Take(100), output.GetProperty("failedIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(53, output.GetProperty("failedIdsOmitted").GetInt32());
        Assert.Equal(153, output.GetProperty("failedCount").GetInt32());
        Assert.Equal(153, output.GetProperty("remainingCount").GetInt32());
        Assert.Equal(1003, output.GetProperty("confirmedCount").GetInt32());
        Assert.Equal("Completed", output.GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task TableEmptied_ExitZero_SuccessTrue()
    {
        var (exit, output) = await Run(5, new DataTruncateResult
        {
            DeletedCount = 5, RemainingCount = 0, Passes = 1, StopReason = DataTruncateStopReason.Completed
        });

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(output.GetProperty("success").GetBoolean());
        Assert.Empty(output.GetProperty("failedIds").EnumerateArray());
        Assert.Equal(0, output.GetProperty("failedIdsOmitted").GetInt32());
    }

    [Fact]
    public async Task Interrupted_ExitTwo_KeepsTheCountsOfWhatWasDeleted()
    {
        var (exit, output) = await Run(10, new DataTruncateResult
        {
            DeletedCount = 4, Passes = 2, RemainingCount = 6, StopReason = DataTruncateStopReason.Interrupted,
            InterruptionCode = ErrorCodes.Query.ExecutionFailed
        });

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(4, output.GetProperty("deletedCount").GetInt32());
        Assert.Equal("Interrupted", output.GetProperty("stopReason").GetString());
    }
}

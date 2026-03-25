using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

public class NoSyncOverAsyncAnalyzerTests
{
    /// <summary>PPDS012: task.Result should flag.</summary>
    [Fact]
    public async Task PPDS012_TaskResult_ReportsWarning()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyService
            {
                void DoWork()
                {
                    var task = Task.FromResult(42);
                    var result = task.Result;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSyncOverAsyncAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS012");
    }

    /// <summary>PPDS012: task.Wait() should flag.</summary>
    [Fact]
    public async Task PPDS012_TaskWait_ReportsWarning()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyService
            {
                void DoWork()
                {
                    var task = Task.Delay(100);
                    task.Wait();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSyncOverAsyncAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS012");
    }

    /// <summary>PPDS012: task.GetAwaiter().GetResult() should flag.</summary>
    [Fact]
    public async Task PPDS012_GetAwaiterGetResult_ReportsWarning()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyService
            {
                void DoWork()
                {
                    var task = Task.FromResult(42);
                    var result = task.GetAwaiter().GetResult();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSyncOverAsyncAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS012");
    }

    /// <summary>PPDS012: Custom class with .Result property should NOT flag.</summary>
    [Fact]
    public async Task PPDS012_NonTaskTypeWithResult_NoDiagnostic()
    {
        const string code = """
            class OperationResult
            {
                public string Result { get; set; }
            }
            class MyService
            {
                void DoWork()
                {
                    var op = new OperationResult();
                    var r = op.Result;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSyncOverAsyncAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS012: await task should NOT flag.</summary>
    [Fact]
    public async Task PPDS012_AwaitedTask_NoDiagnostic()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyService
            {
                async Task DoWorkAsync()
                {
                    var result = await Task.FromResult(42);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSyncOverAsyncAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS012: valueTask.Result should flag.</summary>
    [Fact]
    public async Task PPDS012_ValueTaskResult_ReportsWarning()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyService
            {
                void DoWork()
                {
                    var vt = new ValueTask<int>(42);
                    var result = vt.Result;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoSyncOverAsyncAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS012");
    }
}

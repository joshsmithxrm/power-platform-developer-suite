using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class NoFireAndForgetInCtorAnalyzerTests
{
    /// <summary>PPDS013: Unawaited async method in ctor should flag.</summary>
    [Fact]
    public async Task PPDS013_UnawaitedAsyncInCtor_ReportsWarning()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyView
            {
                public MyView()
                {
                    LoadAsync();
                }
                private Task LoadAsync() => Task.CompletedTask;
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoFireAndForgetInCtorAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS013");
    }

    /// <summary>PPDS013: Awaited call in ctor should NOT flag (even though ctors can't be async, tests the pattern).</summary>
    [Fact]
    public async Task PPDS013_AwaitedCallInCtor_NoDiagnostic()
    {
        // Constructors can't be async in C#, so we use a non-ctor async method
        // to verify the analyzer only targets constructors.
        const string code = """
            using System.Threading.Tasks;
            class MyView
            {
                async Task InitAsync()
                {
                    await LoadAsync();
                }
                private Task LoadAsync() => Task.CompletedTask;
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoFireAndForgetInCtorAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS013: Task.FromResult in ctor should NOT flag (safe pattern).</summary>
    [Fact]
    public async Task PPDS013_TaskFromResult_NoDiagnostic()
    {
        const string code = """
            using System.Threading.Tasks;
            class MyView
            {
                private Task<int> _cached;
                public MyView()
                {
                    _cached = Task.FromResult(42);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoFireAndForgetInCtorAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS013: ContinueWith error handling should NOT flag.</summary>
    [Fact]
    public async Task PPDS013_ContinueWithErrorHandling_NoDiagnostic()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            class MyView
            {
                public MyView()
                {
                    _ = LoadAsync().ContinueWith(t =>
                    {
                        if (t.IsFaulted) Console.WriteLine(t.Exception);
                    });
                }
                private Task LoadAsync() => Task.CompletedTask;
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoFireAndForgetInCtorAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS013: Non-async (void-returning) method in ctor should NOT flag.</summary>
    [Fact]
    public async Task PPDS013_NonAsyncMethodInCtor_NoDiagnostic()
    {
        const string code = """
            class MyView
            {
                public MyView()
                {
                    Initialize();
                }
                private void Initialize() { }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoFireAndForgetInCtorAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>PPDS013: FireAndForget helper wrapper should NOT flag.</summary>
    [Fact]
    public async Task PPDS013_FireAndForgetHelper_NoDiagnostic()
    {
        const string code = """
            using System.Threading.Tasks;
            class ErrorService
            {
                public void FireAndForget(Task task, string context) { }
            }
            class MyView
            {
                private ErrorService _errorService = new ErrorService();
                public MyView()
                {
                    _errorService.FireAndForget(LoadAsync(), "context");
                }
                private Task LoadAsync() => Task.CompletedTask;
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<NoFireAndForgetInCtorAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }
}

using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

public class PropagateCancellationAnalyzerTests
{
    /// <summary>AC-13: Flags awaited call missing CancellationToken when overload exists.</summary>
    [Fact]
    public async Task FlagsMissingCancellationToken()
    {
        const string code = """
            using System.Threading;
            using System.Threading.Tasks;
            class Downstream
            {
                public Task<string> GetAsync() => Task.FromResult("a");
                public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("a");
            }
            class Service
            {
                private Downstream _d = new();
                async Task DoWork(CancellationToken cancellationToken)
                {
                    await _d.GetAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PropagateCancellationAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS011");
    }

    /// <summary>AC-14: Does NOT flag when method has no CancellationToken parameter.</summary>
    [Fact]
    public async Task DoesNotFlagMethodWithoutCancellationToken()
    {
        const string code = """
            using System.Threading;
            using System.Threading.Tasks;
            class Downstream
            {
                public Task<string> GetAsync() => Task.FromResult("a");
                public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("a");
            }
            class Service
            {
                private Downstream _d = new();
                async Task DoWork()
                {
                    await _d.GetAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PropagateCancellationAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-15: Does NOT flag when called method has no CancellationToken overload.</summary>
    [Fact]
    public async Task DoesNotFlagWhenNoOverloadExists()
    {
        const string code = """
            using System.Threading;
            using System.Threading.Tasks;
            class Downstream
            {
                public Task<string> GetAsync() => Task.FromResult("a");
            }
            class Service
            {
                private Downstream _d = new();
                async Task DoWork(CancellationToken cancellationToken)
                {
                    await _d.GetAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PropagateCancellationAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-16: Does NOT flag when CancellationToken is already passed.</summary>
    [Fact]
    public async Task DoesNotFlagWhenTokenAlreadyPassed()
    {
        const string code = """
            using System.Threading;
            using System.Threading.Tasks;
            class Downstream
            {
                public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("a");
            }
            class Service
            {
                private Downstream _d = new();
                async Task DoWork(CancellationToken cancellationToken)
                {
                    await _d.GetAsync(cancellationToken);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PropagateCancellationAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotFlagInsideNestedLambda()
    {
        const string code = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Downstream
            {
                public Task<string> GetAsync() => Task.FromResult("a");
                public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("a");
            }
            class Service
            {
                private Downstream _d = new();
                async Task DoWork(CancellationToken cancellationToken)
                {
                    Func<Task> f = async () => await _d.GetAsync();
                    await f();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PropagateCancellationAnalyzer>(code);

        // The lambda call is in a nested scope — not flagged
        // The f() call has no CT overload — not flagged
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task FlagsMethodWithDefaultCancellationTokenParam()
    {
        const string code = """
            using System.Threading;
            using System.Threading.Tasks;
            class Downstream
            {
                public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult("a");
            }
            class Service
            {
                private Downstream _d = new();
                async Task DoWork(CancellationToken cancellationToken)
                {
                    await _d.GetAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PropagateCancellationAnalyzer>(code);

        // The called method has a CancellationToken param (even though default)
        // and we're not passing our token — should flag
        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS011");
    }
}

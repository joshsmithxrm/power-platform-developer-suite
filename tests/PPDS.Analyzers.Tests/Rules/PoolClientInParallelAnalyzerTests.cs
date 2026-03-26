using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class PoolClientInParallelAnalyzerTests
{
    /// <summary>AC-07: Flags pooled client used across 2+ await calls.</summary>
    [Fact]
    public async Task FlagsClientWithMultipleAwaits()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            interface IPooledClient : IAsyncDisposable
            {
                Task<object> RetrieveMultipleAsync(string query);
                Task<Guid> CreateAsync(object entity);
            }
            interface IPool { Task<IPooledClient> GetClientAsync(); }
            class Service
            {
                private IPool _pool;
                async Task DoWork()
                {
                    var client = await _pool.GetClientAsync();
                    await client.RetrieveMultipleAsync("q1");
                    await client.CreateAsync(new object());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS007");
    }

    /// <summary>AC-08: Does NOT flag single-await client usage.</summary>
    [Fact]
    public async Task DoesNotFlagSingleAwait()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            interface IPooledClient : IAsyncDisposable
            {
                Task<object> RetrieveMultipleAsync(string query);
            }
            interface IPool { Task<IPooledClient> GetClientAsync(); }
            class Service
            {
                private IPool _pool;
                async Task DoWork()
                {
                    var client = await _pool.GetClientAsync();
                    await client.RetrieveMultipleAsync("q1");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotFlagNonPoolVariable()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            class SomeClient
            {
                public Task<string> GetAsync() => Task.FromResult("a");
                public Task<string> PostAsync() => Task.FromResult("b");
            }
            class Service
            {
                async Task DoWork()
                {
                    var client = new SomeClient();
                    await client.GetAsync();
                    await client.PostAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task FlagsClientInUsingStatement()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            interface IPooledClient : IAsyncDisposable
            {
                Task<object> RetrieveMultipleAsync(string query);
                Task<Guid> CreateAsync(object entity);
            }
            interface IPool { Task<IPooledClient> GetClientAsync(); }
            class Service
            {
                private IPool _pool;
                async Task DoWork()
                {
                    using (var client = await _pool.GetClientAsync())
                    {
                        await client.RetrieveMultipleAsync("q1");
                        await client.CreateAsync(new object());
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS007");
    }

    [Fact]
    public async Task DoesNotFlagSingleAwaitInUsingStatement()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            interface IPooledClient : IAsyncDisposable
            {
                Task<object> RetrieveMultipleAsync(string query);
            }
            interface IPool { Task<IPooledClient> GetClientAsync(); }
            class Service
            {
                private IPool _pool;
                async Task DoWork()
                {
                    using (var client = await _pool.GetClientAsync())
                    {
                        await client.RetrieveMultipleAsync("q1");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotFlagAwaitInsideNestedLambda()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            interface IPooledClient : IAsyncDisposable
            {
                Task<object> RetrieveMultipleAsync(string query);
            }
            interface IPool { Task<IPooledClient> GetClientAsync(); }
            class Service
            {
                private IPool _pool;
                async Task DoWork()
                {
                    var client = await _pool.GetClientAsync();
                    await client.RetrieveMultipleAsync("q1");
                    Func<Task> f = async () => await client.RetrieveMultipleAsync("q2");
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        // Second await is inside lambda — doesn't count, so only 1 await in scope
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task FlagsClientWithConfigureAwait()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            interface IPooledClient : IAsyncDisposable
            {
                Task<object> RetrieveMultipleAsync(string query);
                Task<Guid> CreateAsync(object entity);
            }
            interface IPool { Task<IPooledClient> GetClientAsync(); }
            class Service
            {
                private IPool _pool;
                async Task DoWork()
                {
                    var client = await _pool.GetClientAsync();
                    await client.RetrieveMultipleAsync("q1").ConfigureAwait(false);
                    await client.CreateAsync(new object()).ConfigureAwait(false);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<PoolClientInParallelAnalyzer>(code);

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS007");
    }
}

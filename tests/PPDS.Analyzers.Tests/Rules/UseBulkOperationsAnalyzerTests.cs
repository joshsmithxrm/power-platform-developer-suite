using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class UseBulkOperationsAnalyzerTests
{
    /// <summary>AC-09: Flags CreateAsync inside for loop.</summary>
    [Fact]
    public async Task FlagsCreateAsyncInForLoop()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationServiceAsync2
                {
                    Task<Guid> CreateAsync(object entity);
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                class Service
                {
                    private IOrganizationServiceAsync2 _client;
                    async Task DoWork()
                    {
                        var items = new object[10];
                        for (int i = 0; i < items.Length; i++)
                        {
                            await _client.CreateAsync(items[i]);
                        }
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseBulkOperationsAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS008");
        diagnostics[0].GetMessage().Should().Contain("CreateMultipleAsync");
    }

    /// <summary>AC-10: Flags DeleteAsync inside foreach loop.</summary>
    [Fact]
    public async Task FlagsDeleteAsyncInForeachLoop()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationServiceAsync2
                {
                    Task DeleteAsync(string entityName, Guid id);
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                class Service
                {
                    private IOrganizationServiceAsync2 _client;
                    async Task DoWork(List<Guid> ids)
                    {
                        foreach (var id in ids)
                        {
                            await _client.DeleteAsync("account", id);
                        }
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseBulkOperationsAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS008");
        diagnostics[0].GetMessage().Should().Contain("DeleteMultipleAsync");
    }

    /// <summary>AC-11: Does NOT flag single calls outside loops.</summary>
    [Fact]
    public async Task DoesNotFlagSingleCallOutsideLoop()
    {
        const string code = """
            using System;
            using System.Threading.Tasks;
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationServiceAsync2
                {
                    Task<Guid> CreateAsync(object entity);
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                class Service
                {
                    private IOrganizationServiceAsync2 _client;
                    async Task DoWork()
                    {
                        await _client.CreateAsync(new object());
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseBulkOperationsAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-12: Message suggests correct bulk alternative.</summary>
    [Fact]
    public async Task MessageSuggestsUpdateMultiple()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationServiceAsync2
                {
                    Task UpdateAsync(object entity);
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                class Service
                {
                    private IOrganizationServiceAsync2 _client;
                    async Task DoWork(List<object> entities)
                    {
                        foreach (var entity in entities)
                        {
                            await _client.UpdateAsync(entity);
                        }
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseBulkOperationsAnalyzer>(code);

        diagnostics.Should().ContainSingle();
        diagnostics[0].GetMessage().Should().Contain("UpdateMultipleAsync");
    }

    [Fact]
    public async Task FlagsSyncDeleteInWhileLoop()
    {
        const string code = """
            using System;
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationService
                {
                    void Delete(string entityName, Guid id);
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                class Service
                {
                    private IOrganizationService _client;
                    void DoWork()
                    {
                        int i = 0;
                        while (i < 10)
                        {
                            _client.Delete("account", Guid.NewGuid());
                            i++;
                        }
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseBulkOperationsAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS008");
    }

    [Fact]
    public async Task FlagsCallOnTypeImplementingInterface()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            namespace Microsoft.Xrm.Sdk
            {
                public interface IOrganizationServiceAsync2
                {
                    Task<Guid> CreateAsync(object entity);
                }
            }
            namespace PPDS.Dataverse
            {
                using Microsoft.Xrm.Sdk;
                public interface IPooledClient : IOrganizationServiceAsync2, IAsyncDisposable { }
            }
            namespace TestCode
            {
                using PPDS.Dataverse;
                class Service
                {
                    async Task DoWork(IPooledClient client, List<object> items)
                    {
                        foreach (var item in items)
                        {
                            await client.CreateAsync(item);
                        }
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseBulkOperationsAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS008");
    }
}

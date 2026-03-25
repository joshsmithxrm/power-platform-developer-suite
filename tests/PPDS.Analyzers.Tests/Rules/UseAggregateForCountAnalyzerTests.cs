using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

public class UseAggregateForCountAnalyzerTests
{
    [Fact]
    public async Task PPDS009_RetrieveMultipleEntitiesCount_ReportsWarning()
    {
        const string code = """
            using System.Collections.Generic;
            namespace Microsoft.Xrm.Sdk
            {
                public class Entity { }
                public class EntityCollection
                {
                    public List<Entity> Entities { get; set; }
                    public int TotalRecordCount { get; set; }
                    public bool TotalRecordCountLimitExceeded { get; set; }
                }
                public interface IOrganizationService
                {
                    EntityCollection RetrieveMultiple(object query);
                }
            }
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression(string name) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                using Microsoft.Xrm.Sdk.Query;
                class Service
                {
                    private IOrganizationService _svc;
                    void DoWork()
                    {
                        var count = _svc.RetrieveMultiple(new QueryExpression("account")).Entities.Count;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseAggregateForCountAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS009");
    }

    [Fact]
    public async Task PPDS009_RetrieveMultipleUsedForData_NoDiagnostic()
    {
        const string code = """
            using System.Collections.Generic;
            namespace Microsoft.Xrm.Sdk
            {
                public class Entity { }
                public class EntityCollection
                {
                    public List<Entity> Entities { get; set; }
                    public int TotalRecordCount { get; set; }
                }
                public interface IOrganizationService
                {
                    EntityCollection RetrieveMultiple(object query);
                }
            }
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression(string name) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                using Microsoft.Xrm.Sdk.Query;
                class Service
                {
                    private IOrganizationService _svc;
                    void DoWork()
                    {
                        var results = _svc.RetrieveMultiple(new QueryExpression("account"));
                        foreach (var e in results.Entities) { }
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseAggregateForCountAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PPDS009_RetrieveMultipleTotalRecordCount_ReportsWarning()
    {
        const string code = """
            using System.Collections.Generic;
            namespace Microsoft.Xrm.Sdk
            {
                public class Entity { }
                public class EntityCollection
                {
                    public List<Entity> Entities { get; set; }
                    public int TotalRecordCount { get; set; }
                }
                public interface IOrganizationService
                {
                    EntityCollection RetrieveMultiple(object query);
                }
            }
            namespace Microsoft.Xrm.Sdk.Query
            {
                public class QueryExpression
                {
                    public QueryExpression(string name) { }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                using Microsoft.Xrm.Sdk.Query;
                class Service
                {
                    private IOrganizationService _svc;
                    void DoWork()
                    {
                        var count = _svc.RetrieveMultiple(new QueryExpression("account")).TotalRecordCount;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseAggregateForCountAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS009");
    }

    [Fact]
    public async Task PPDS009_NonRetrieveMultipleCount_NoDiagnostic()
    {
        const string code = """
            using System.Collections.Generic;
            class Service
            {
                void DoWork()
                {
                    var list = new List<string> { "a", "b" };
                    var count = list.Count;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseAggregateForCountAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }
}

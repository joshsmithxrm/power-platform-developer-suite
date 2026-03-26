using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class ValidateTopCountAnalyzerTests
{
    [Fact]
    public async Task PPDS010_QueryExpressionWithoutTopCount_ReportsWarning()
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
                    public int? TopCount { get; set; }
                }
                public class FetchExpression
                {
                    public FetchExpression(string fetch) { }
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
                        var qe = new QueryExpression("account");
                        _svc.RetrieveMultiple(qe);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS010");
    }

    [Fact]
    public async Task PPDS010_QueryExpressionWithTopCount_NoDiagnostic()
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
                    public int? TopCount { get; set; }
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
                        var qe = new QueryExpression("account");
                        qe.TopCount = 50;
                        _svc.RetrieveMultiple(qe);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PPDS010_InlineQueryWithTopCountInitializer_NoDiagnostic()
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
                    public int? TopCount { get; set; }
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
                        _svc.RetrieveMultiple(new QueryExpression("account") { TopCount = 10 });
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PPDS010_InlineQueryWithoutTopCount_ReportsWarning()
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
                    public int? TopCount { get; set; }
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
                        _svc.RetrieveMultiple(new QueryExpression("account"));
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS010",
            "inline QueryExpression without TopCount initializer should flag");
    }

    [Fact]
    public async Task PPDS010_TopCountAssignedAfterCall_ReportsWarning()
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
                    public int? TopCount { get; set; }
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
                        var qe = new QueryExpression("account");
                        _svc.RetrieveMultiple(qe);
                        qe.TopCount = 50;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().ContainSingle(d => d.Id == "PPDS010",
            "TopCount assigned AFTER RetrieveMultiple should still flag");
    }

    [Fact]
    public async Task PPDS010_MethodCallArgument_NoDiagnostic()
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
                    public int? TopCount { get; set; }
                }
            }
            namespace TestCode
            {
                using Microsoft.Xrm.Sdk;
                using Microsoft.Xrm.Sdk.Query;
                class Service
                {
                    private IOrganizationService _svc;
                    QueryExpression GetQuery() => new QueryExpression("account") { TopCount = 10 };
                    void DoWork()
                    {
                        _svc.RetrieveMultiple(GetQuery());
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().BeEmpty("method call arguments can't be statically analyzed — skip to avoid false positives");
    }

    [Fact]
    public async Task PPDS010_NonQueryExpressionArg_NoDiagnostic()
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
                public class FetchExpression
                {
                    public FetchExpression(string fetch) { }
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
                        var fetch = new FetchExpression("<fetch/>");
                        _svc.RetrieveMultiple(fetch);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<ValidateTopCountAnalyzer>(code);

        diagnostics.Should().BeEmpty();
    }
}

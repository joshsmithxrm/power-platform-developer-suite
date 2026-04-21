using System.Linq;
using FluentAssertions;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Roles;
using PPDS.Cli.Services.SolutionComponents;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.Users;
using PPDS.Cli.Services.WebResources;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Architecture;

/// <summary>
/// Architecture tests (AC-14..AC-26) verifying that the 12 domain services relocated out of
/// <c>PPDS.Dataverse</c> live in <c>PPDS.Cli.Services.&lt;Area&gt;</c>, and that the
/// <c>PPDS.Dataverse</c> library is free of domain-service leakage and has no reference back to
/// <c>PPDS.Cli</c>. Without these tests a future refactor could silently re-introduce the A1
/// constitutional violation (infrastructure library holding application services).
/// </summary>
public class AssemblyLocationTests
{
    // PPDS.Cli.csproj ships with <AssemblyName>ppds</AssemblyName> so the CLI tool can be invoked
    // as `ppds <command>` after a `dotnet tool install`. The AC text says "lives in PPDS.Cli
    // assembly" — the spirit is that it lives in the CLI assembly, which is this one.
    private const string CliAssemblyName = "ppds";

    [Fact]
    public void PluginTraceService_LivesInCliAssembly()
    {
        typeof(IPluginTraceService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IPluginTraceService).Namespace.Should().Be("PPDS.Cli.Services.PluginTraces");
    }

    [Fact]
    public void WebResourceService_LivesInCliAssembly()
    {
        typeof(IWebResourceService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IWebResourceService).Namespace.Should().Be("PPDS.Cli.Services.WebResources");
    }

    [Fact]
    public void EnvironmentVariableService_LivesInCliAssembly()
    {
        typeof(IEnvironmentVariableService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IEnvironmentVariableService).Namespace.Should().Be("PPDS.Cli.Services.EnvironmentVariables");
    }

    [Fact]
    public void SolutionService_LivesInCliAssembly()
    {
        typeof(ISolutionService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(ISolutionService).Namespace.Should().Be("PPDS.Cli.Services.Solutions");
    }

    [Fact]
    public void ImportJobService_LivesInCliAssembly()
    {
        typeof(IImportJobService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IImportJobService).Namespace.Should().Be("PPDS.Cli.Services.ImportJobs");
    }

    [Fact]
    public void MetadataAuthoringService_LivesInCliAssembly()
    {
        typeof(IMetadataAuthoringService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IMetadataAuthoringService).Namespace.Should().Be("PPDS.Cli.Services.Metadata.Authoring");
    }

    [Fact]
    public void UserService_LivesInCliAssembly()
    {
        typeof(IUserService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IUserService).Namespace.Should().Be("PPDS.Cli.Services.Users");
    }

    [Fact]
    public void RoleService_LivesInCliAssembly()
    {
        typeof(IRoleService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IRoleService).Namespace.Should().Be("PPDS.Cli.Services.Roles");
    }

    [Fact]
    public void FlowService_LivesInCliAssembly()
    {
        typeof(IFlowService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IFlowService).Namespace.Should().Be("PPDS.Cli.Services.Flows");
    }

    [Fact]
    public void ConnectionReferenceService_LivesInCliAssembly()
    {
        typeof(IConnectionReferenceService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IConnectionReferenceService).Namespace.Should().Be("PPDS.Cli.Services.ConnectionReferences");
    }

    [Fact]
    public void DeploymentSettingsService_LivesInCliAssembly()
    {
        typeof(IDeploymentSettingsService).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IDeploymentSettingsService).Namespace.Should().Be("PPDS.Cli.Services.DeploymentSettings");
    }

    [Fact]
    public void ComponentNameResolver_LivesInCliAssembly()
    {
        typeof(IComponentNameResolver).Assembly.GetName().Name.Should().Be(CliAssemblyName);
        typeof(IComponentNameResolver).Namespace.Should().Be("PPDS.Cli.Services.SolutionComponents");
    }

    /// <summary>
    /// Ensures no domain-service type leaks back into <c>PPDS.Dataverse</c> and that the
    /// infrastructure library does not reference <c>PPDS.Cli</c> — which would re-couple the
    /// layers and re-introduce the A1 violation.
    /// </summary>
    [Fact]
    public void Dataverse_NoDomainServicesOrCliReferences()
    {
        var dataverseAssembly = typeof(IDataverseConnectionPool).Assembly;

        string[] forbiddenTypeNames =
        {
            "IPluginTraceService", "PluginTraceService", "TimelineHierarchyBuilder",
            "IWebResourceService", "WebResourceService",
            "IEnvironmentVariableService", "EnvironmentVariableService",
            "ISolutionService", "SolutionService",
            "IImportJobService", "ImportJobService",
            "IMetadataAuthoringService", "DataverseMetadataAuthoringService",
            "IUserService", "UserService",
            "IRoleService", "RoleService",
            "IFlowService", "FlowService",
            "IConnectionReferenceService", "ConnectionReferenceService",
            "IDeploymentSettingsService", "DeploymentSettingsService",
            "IComponentNameResolver", "ComponentNameResolver",
        };

        var dataverseTypeNames = dataverseAssembly.GetTypes().Select(t => t.Name).ToHashSet();
        var leakedTypes = forbiddenTypeNames.Where(dataverseTypeNames.Contains).ToList();
        leakedTypes.Should().BeEmpty(
            "PPDS.Dataverse must not host domain/application services (constitution A1). " +
            "Leaked types: " + string.Join(", ", leakedTypes));

        dataverseAssembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == CliAssemblyName,
                "PPDS.Dataverse is the infrastructure library and must never depend on PPDS.Cli.");
    }
}

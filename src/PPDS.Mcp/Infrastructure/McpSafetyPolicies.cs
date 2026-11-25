using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services.Environment;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Safety policies that differ on the MCP server from the CLI, TUI and daemon. Registered on the host
/// container and on every per-call service provider, so each copy of the CLI services sees them.
/// </summary>
public static class McpSafetyPolicies
{
    /// <summary>
    /// Registers the MCP policies: an agent never lowers an environment's write protection, confirmed
    /// or not (#1829). The shared <see cref="IEnvironmentConfigService"/> reads the policy.
    /// </summary>
    public static IServiceCollection AddMcpSafetyPolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(ProtectionLoweringPolicy.Refuse);
        return services;
    }
}

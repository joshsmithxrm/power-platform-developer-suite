using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Base class for all MCP tools. Gates service provider access behind parameter validation,
/// making it impossible to reach Dataverse without validating required parameters first.
/// </summary>
public abstract class McpToolBase
{
    /// <summary>
    /// The shared MCP tool context providing access to profiles, pools, and services.
    /// </summary>
    protected McpToolContext Context { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpToolBase"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    protected McpToolBase(McpToolContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Validates required parameters, then creates a service provider for the active profile.
    /// This is the only way for tools to obtain a service provider, ensuring parameter
    /// validation always happens before the expensive profile/connection setup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="requiredParams">
    /// Name-value pairs for required parameters. Null values throw <see cref="ArgumentNullException"/>,
    /// empty/whitespace strings throw <see cref="ArgumentException"/>.
    /// </param>
    /// <returns>A service provider configured for the active profile's environment.</returns>
    protected async Task<ServiceProvider> CreateScopeAsync(
        CancellationToken cancellationToken,
        params (string name, object? value)[] requiredParams)
    {
        foreach (var (name, value) in requiredParams)
        {
            ArgumentNullException.ThrowIfNull(value, name);

            if (value is string s && string.IsNullOrWhiteSpace(s))
                throw new ArgumentException($"'{name}' cannot be empty.", name);
        }

        return await Context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
    }
}

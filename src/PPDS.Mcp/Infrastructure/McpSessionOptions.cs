namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Configuration options for an MCP server session.
/// Parsed from command-line arguments at startup.
/// </summary>
public sealed class McpSessionOptions
{
    /// <summary>
    /// Lock the session to a specific profile name.
    /// If null, uses the active profile at first tool invocation.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Lock the session to a specific environment URL.
    /// If null, uses the profile's default environment.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// When true, all DML operations (INSERT, UPDATE, DELETE) are rejected.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Allowed environment URLs for ppds_env_select. If empty, environment
    /// switching is disabled (locked to the initial environment).
    /// </summary>
    public List<string> AllowedEnvironments { get; set; } = new();

    /// <summary>
    /// Checks whether the given URL is in the allowed environments list.
    /// </summary>
    public bool IsEnvironmentAllowed(string url)
    {
        if (AllowedEnvironments.Count == 0)
            return false; // No allowlist = no switching

        var normalized = url.TrimEnd('/').ToLowerInvariant();
        return AllowedEnvironments.Any(e =>
            e.TrimEnd('/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses command-line arguments into session options.
    /// Unknown args are passed through to the host builder.
    /// </summary>
    public static McpSessionOptions Parse(string[] args)
    {
        var options = new McpSessionOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length:
                    options.Profile = args[++i];
                    break;
                case "--environment" when i + 1 < args.Length:
                    options.Environment = args[++i];
                    break;
                case "--read-only":
                    options.ReadOnly = true;
                    break;
                case "--allowed-env" when i + 1 < args.Length:
                    options.AllowedEnvironments.Add(args[++i]);
                    break;
            }
        }

        return options;
    }
}

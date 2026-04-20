using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Services;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Mcp.Infrastructure;

// MCP servers MUST NOT write to stdout (reserved for protocol).
// Redirect all console output to stderr before any logging occurs.
Console.SetOut(Console.Error);

// Parse session options before building the host.
var sessionOptions = McpSessionOptions.Parse(args);

// Resolve the log level from --log-level / PPDS_MCP_LOG_LEVEL. Falls back to Warning so
// MCP servers stay quiet by default (the prior hardcoded behaviour), but operators can
// now crank the level up without rebuilding.
var logLevel = ResolveLogLevel(args);

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr only (stdout is reserved for MCP protocol messages).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(logLevel);

// Register MCP server with stdio transport.
// WithToolsFromAssembly() discovers all [McpServerToolType] classes automatically.
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Register MCP infrastructure.
builder.Services.AddSingleton(sessionOptions);
builder.Services.AddSingleton<IMcpConnectionPoolManager, McpConnectionPoolManager>();
builder.Services.AddSingleton<McpToolContext>();

// Register auth services (ProfileStore, EnvironmentConfigStore, ISecureCredentialStore).
builder.Services.AddAuthServices();

// Register Dataverse infrastructure (pool-adjacent: IMetadataQueryService, IQueryExecutor,
// IBulkOperationExecutor, ICachedMetadataProvider, SchemaValidator, IThrottleTracker).
builder.Services.RegisterDataverseServices();

// Register CLI application services (domain services: IPluginTraceService, ISolutionService,
// IWebResourceService, etc., plus IShakedownGuard and friends). MCP reuses the single CLI
// registration to guarantee the same wiring across CLI / TUI / MCP surfaces (per SL2).
builder.Services.AddCliApplicationServices();

var host = builder.Build();
await host.RunAsync();

// --- helpers -----------------------------------------------------------------

static LogLevel ResolveLogLevel(string[] args)
{
    // --log-level wins over env-var so the operator can override on an ad-hoc launch.
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, "--log-level", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && TryParseLogLevel(args[i + 1], out var fromFlag))
            {
                return fromFlag;
            }
        }
        else if (arg.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg.Substring("--log-level=".Length);
            if (TryParseLogLevel(value, out var fromFlag))
            {
                return fromFlag;
            }
        }
    }

    var envValue = Environment.GetEnvironmentVariable("PPDS_MCP_LOG_LEVEL");
    if (!string.IsNullOrWhiteSpace(envValue) && TryParseLogLevel(envValue, out var fromEnv))
    {
        return fromEnv;
    }

    return LogLevel.Warning;
}

static bool TryParseLogLevel(string raw, out LogLevel level)
{
    // Accept the standard Microsoft.Extensions.Logging names case-insensitively.
    // Enum.TryParse with ignoreCase=true handles: Trace/Debug/Information/Warning/Error/Critical/None.
    if (Enum.TryParse<LogLevel>(raw, ignoreCase: true, out level) &&
        Enum.IsDefined(typeof(LogLevel), level))
    {
        return true;
    }

    level = LogLevel.Warning;
    return false;
}

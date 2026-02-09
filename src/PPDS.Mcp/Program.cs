using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PPDS.Auth.DependencyInjection;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Mcp.Infrastructure;

// MCP servers MUST NOT write to stdout (reserved for protocol).
// Redirect all console output to stderr before any logging occurs.
Console.SetOut(Console.Error);

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr only (stdout is reserved for MCP protocol messages).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Register MCP server with stdio transport.
// WithToolsFromAssembly() discovers all [McpServerToolType] classes automatically.
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Register MCP infrastructure.
builder.Services.AddSingleton<IMcpConnectionPoolManager, McpConnectionPoolManager>();
builder.Services.AddSingleton<McpToolContext>();

// Register auth services (ProfileStore, EnvironmentConfigStore, ISecureCredentialStore).
builder.Services.AddAuthServices();

// Register Dataverse services (IMetadataService, IPluginTraceService, IQueryExecutor, etc.).
builder.Services.RegisterDataverseServices();

var host = builder.Build();
await host.RunAsync();

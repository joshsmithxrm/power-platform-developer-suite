using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Logging;
using StreamJsonRpc;

namespace PPDS.Cli.Commands.Serve;

/// <summary>
/// The 'serve' command starts the CLI as a long-running daemon process,
/// communicating via JSON-RPC over stdio for IDE integration.
/// </summary>
public static class ServeCommand
{
    /// <summary>
    /// Creates the 'serve' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("serve", "Start daemon for IDE integration (JSON-RPC over stdio)")
        {
            Hidden = true // IDE-callable but not user-facing
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            return await ExecuteAsync(cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        // Open stdin/stdout as raw streams for JSON-RPC communication
        // IMPORTANT: Do not use Console.WriteLine in this mode - it corrupts the JSON-RPC stream
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        // Splice the two simplex streams into a single duplex stream
        var duplexStream = FullDuplexStream.Splice(stdin, stdout);

        // Create a logger factory for daemon serve mode.
        // Logs go to stderr (stdout is reserved for JSON-RPC).
        var loggerOptions = new CliLoggerOptions
        {
            MinimumLevel = LogLevel.Information,
            UseJsonFormat = false,
            EnableColors = !Console.IsErrorRedirected
        };
        var logServices = new ServiceCollection();
        logServices.AddCliLogging(loggerOptions);
        await using var logServiceProvider = logServices.BuildServiceProvider();
        var loggerFactory = logServiceProvider.GetRequiredService<ILoggerFactory>();

        // Create the pool manager with daemon lifetime - caches connection pools across RPC calls
        await using var poolManager = new DaemonConnectionPoolManager(loggerFactory);

        // Create a lightweight auth service provider for profile/credential store access
        await using var authProvider = ProfileServiceFactory.CreateLocalProvider();

        // Create the RPC target that handles method calls. Dispose cancels the daemon-lifetime
        // CancellationTokenSource used by fire-and-forget background tasks.
        using var handler = new RpcMethodHandler(poolManager, authProvider, loggerFactory.CreateLogger<RpcMethodHandler>());

        // Attach JSON-RPC to the duplex stream with our handler.
        // SECURITY: TraceSource is intentionally NOT configured here. StreamJsonRpc trace logging
        // would capture full request payloads, including plain-text secrets passed to profiles/create
        // (clientSecret, password, certificatePassword). Keep trace logging disabled in production.
        //
        // Use SystemTextJsonFormatter so that [JsonPropertyName] attributes on response DTOs are
        // honored. The default JsonRpc.Attach(Stream) uses JsonMessageFormatter (Newtonsoft.Json)
        // which ignores System.Text.Json attributes, producing PascalCase keys instead of camelCase.
        var formatter = new SystemTextJsonFormatter();
        var messageHandler = new HeaderDelimitedMessageHandler(duplexStream, duplexStream, formatter);
        using var rpc = new JsonRpc(messageHandler);
        rpc.AddLocalRpcTarget(handler);
        rpc.StartListening();

        // Set RPC context for device code notifications
        handler.SetRpcContext(rpc);

        // Register for cancellation to allow graceful shutdown
        using var registration = cancellationToken.Register(() => rpc.Dispose());

        try
        {
            // Wait until the connection drops (stdin closes) or cancellation is requested
            await rpc.Completion;
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown via cancellation token
            return ExitCodes.Success;
        }
        catch (Exception)
        {
            // Connection closed unexpectedly - not necessarily an error
            // (e.g., client process terminated)
            return ExitCodes.Success;
        }
    }
}

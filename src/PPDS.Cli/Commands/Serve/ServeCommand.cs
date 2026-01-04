using System.CommandLine;
using Nerdbank.Streams;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
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

        // Create the pool manager with daemon lifetime - caches connection pools across RPC calls
        await using var poolManager = new DaemonConnectionPoolManager();

        // Create the RPC target that handles method calls
        var handler = new RpcMethodHandler(poolManager);

        // Attach JSON-RPC to the duplex stream with our handler
        using var rpc = JsonRpc.Attach(duplexStream, handler);

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

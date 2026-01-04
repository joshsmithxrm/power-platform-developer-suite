using System.Text.Json.Serialization;
using PPDS.Auth.Credentials;
using StreamJsonRpc;

namespace PPDS.Cli.Commands.Serve.Handlers;

/// <summary>
/// Handles device code display in daemon context by sending RPC notifications.
/// </summary>
internal static class DaemonDeviceCodeHandler
{
    /// <summary>
    /// Creates a device code callback that sends notifications via JSON-RPC.
    /// </summary>
    /// <param name="rpc">The JSON-RPC connection to send notifications on.</param>
    /// <returns>A callback that sends device code info as RPC notification.</returns>
    public static Action<DeviceCodeInfo> CreateCallback(JsonRpc? rpc)
    {
        return async info =>
        {
            if (rpc == null)
            {
                return;
            }

            try
            {
                // Fire-and-forget notification to client (VS Code extension)
                await rpc.NotifyAsync("auth/deviceCode", new DeviceCodeNotification
                {
                    UserCode = info.UserCode,
                    VerificationUrl = info.VerificationUrl,
                    Message = info.Message
                });
            }
            catch
            {
                // Ignore exceptions from fire-and-forget notifications.
                // This can happen if the client disconnects during auth.
            }
        };
    }
}

/// <summary>
/// Device code notification sent to the client.
/// </summary>
public class DeviceCodeNotification
{
    /// <summary>
    /// Gets or sets the user code to enter.
    /// </summary>
    [JsonPropertyName("userCode")]
    public string UserCode { get; set; } = "";

    /// <summary>
    /// Gets or sets the URL where the user should enter the code.
    /// </summary>
    [JsonPropertyName("verificationUrl")]
    public string VerificationUrl { get; set; } = "";

    /// <summary>
    /// Gets or sets the full message to display to the user.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

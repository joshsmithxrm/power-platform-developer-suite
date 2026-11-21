using System;
using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace PPDS.Internal;

/// <summary>
/// Lets a host forbid interactive sign-in (system browser, device code, or a Dataverse SDK OAuth prompt)
/// for the whole process.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into PPDS.Auth and PPDS.Dataverse from one source file. Every interactive token acquisition
/// in those libraries, and every place they hand a connection string to the Dataverse SDK, calls this
/// guard first.
/// </para>
/// <para>
/// Off unless the host sets the AppContext switch <see cref="SwitchName"/> (for example through
/// <c>RuntimeHostConfigurationOption</c>). The repository's test projects set it so that no test can
/// open a browser or wait for a device code. When it is on, a blocked attempt throws
/// <see cref="InvalidOperationException"/>; if the host has also stored a
/// <see cref="ConcurrentQueue{T}"/> of <see cref="string"/> under <see cref="BlockedAttemptsDataName"/>
/// with <see cref="AppContext.SetData"/>, the message is added to it first, so the host can see attempts
/// that calling code caught.
/// </para>
/// </remarks>
internal static class InteractiveSignInGuard
{
    /// <summary>The AppContext switch that blocks interactive sign-in in this process.</summary>
    internal const string SwitchName = "PPDS.BlockInteractiveSignIn";

    /// <summary>The AppContext data name under which a host may store a queue that receives each blocked attempt.</summary>
    internal const string BlockedAttemptsDataName = "PPDS.BlockedInteractiveSignIns";

    /// <summary>Gets whether interactive sign-in is blocked in this process.</summary>
    internal static bool IsBlocked =>
        AppContext.TryGetSwitch(SwitchName, out var blocked) && blocked;

    /// <summary>
    /// Throws when interactive sign-in is blocked in this process.
    /// </summary>
    /// <param name="flow">The flow about to start, for the message (for example "browser" or "device code").</param>
    /// <exception cref="InvalidOperationException">Interactive sign-in is blocked.</exception>
    internal static void ThrowIfBlocked(string flow)
    {
        if (!IsBlocked)
        {
            return;
        }

        var message =
            $"Interactive sign-in ({flow}) is disabled in this process by the AppContext switch '{SwitchName}'. " +
            "Remove the switch from the host's runtime configuration to allow browser, device-code and OAuth prompt sign-in.";
        (AppContext.GetData(BlockedAttemptsDataName) as ConcurrentQueue<string>)?.Enqueue(message);
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Throws when interactive sign-in is blocked and the connection string uses OAuth, for which the
    /// Dataverse SDK may show its own sign-in prompt.
    /// </summary>
    /// <param name="connectionString">The connection string about to be passed to the Dataverse SDK.</param>
    /// <exception cref="InvalidOperationException">Interactive sign-in is blocked and the connection string uses OAuth.</exception>
    internal static void ThrowIfInteractiveConnectionString(string? connectionString)
    {
        if (IsBlocked && UsesOAuth(connectionString))
        {
            ThrowIfBlocked("OAuth connection string");
        }
    }

    /// <summary>Gets whether the Dataverse SDK would read a connection string as OAuth authentication.</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>
    /// <see langword="true"/> when the SDK's rule selects <see cref="AuthenticationType.OAuth"/>: the value of
    /// <c>AuthType</c>, or of <c>AuthenticationType</c> when there is no <c>AuthType</c>, parsed as an
    /// <see cref="AuthenticationType"/> name or number (so <c>AuthType=5</c> is OAuth). Also
    /// <see langword="true"/> when the string cannot be parsed and mentions OAuth.
    /// </returns>
    internal static bool UsesOAuth(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            object? value = null;
            if (!builder.TryGetValue("AuthType", out value))
            {
                builder.TryGetValue("AuthenticationType", out value);
            }

            // Case-insensitive, so it also refuses "oauth", which the SDK itself rejects as invalid.
            return Enum.TryParse<AuthenticationType>(value?.ToString()?.Trim(), ignoreCase: true, out var authType)
                && authType == AuthenticationType.OAuth;
        }
        catch (ArgumentException)
        {
            // Unparseable here: decide conservatively.
            return connectionString.IndexOf("OAuth", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

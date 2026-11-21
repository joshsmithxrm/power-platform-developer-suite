using System;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Lets a process forbid interactive sign-in (system browser or device code).
/// </summary>
/// <remarks>
/// Off unless the <see cref="SwitchName"/> AppContext switch is set. The unit test projects set it in
/// their runtime configuration, so a unit test that reaches a real interactive sign-in fails at once
/// instead of opening a browser or waiting for someone to enter a device code.
/// Every interactive token acquisition in this assembly calls <see cref="ThrowIfBlocked"/> first.
/// </remarks>
internal static class InteractiveSignInGuard
{
    /// <summary>The AppContext switch that blocks interactive sign-in in this process.</summary>
    internal const string SwitchName = "PPDS.Auth.BlockInteractiveSignIn";

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
        if (IsBlocked)
        {
            throw new InvalidOperationException(
                $"Interactive sign-in ({flow}) is blocked in this process by the AppContext switch '{SwitchName}'. " +
                "Unit tests set it so that no test can open a browser or wait for a device code; " +
                "replace the credential or discovery service with a fake.");
        }
    }
}

using System.Collections.Generic;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;

namespace PPDS.Cli.Tests.Services.Shared;

/// <summary>
/// Test fake that simulates an active shakedown session — every call to
/// <see cref="EnsureCanMutate"/> throws <see cref="PpdsException"/> with
/// <see cref="ErrorCodes.Safety.ShakedownActive"/>. Used by per-service
/// guard tests to verify that every mutation method funnels through the
/// guard (AC-32 through AC-38).
/// </summary>
public sealed class ActiveFakeShakedownGuard : IShakedownGuard
{
    /// <inheritdoc />
    public void EnsureCanMutate(string operationDescription)
        => throw new PpdsException(
            ErrorCodes.Safety.ShakedownActive,
            $"Mutation '{operationDescription}' refused: shakedown session is active (test fake).",
            new Dictionary<string, object>
            {
                ["operation"] = operationDescription,
                ["activationSource"] = "fake"
            });
}

/// <summary>
/// Test fake that simulates an inactive shakedown session — every call to
/// <see cref="EnsureCanMutate"/> returns normally without side effects.
/// Used by existing service unit tests that construct services directly
/// but do not exercise guard behavior.
/// </summary>
public sealed class InactiveFakeShakedownGuard : IShakedownGuard
{
    /// <inheritdoc />
    public void EnsureCanMutate(string operationDescription)
    {
        // No-op — shakedown is inactive in this fake.
    }
}

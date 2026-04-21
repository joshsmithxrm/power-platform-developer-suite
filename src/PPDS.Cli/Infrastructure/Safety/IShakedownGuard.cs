using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Domain-layer safety primitive that refuses mutations while a shakedown
/// session is active. Every mutation-owning Application Service injects this
/// and calls <see cref="EnsureCanMutate"/> at the top of every mutation
/// method — CLI, TUI, Extension daemon RPC, and MCP all traverse the same
/// gate (constitution A2).
/// </summary>
/// <remarks>
/// Activation is driven by either the <c>PPDS_SHAKEDOWN</c> environment
/// variable (value <c>"1"</c>) or a sentinel file at
/// <c>.claude/state/shakedown-active.json</c> with a fresh <c>started_at</c>
/// timestamp (≤24h old). Activation state is cached internally for ≤5s so
/// high-throughput bulk operations do not pay a per-call file stat.
/// </remarks>
public interface IShakedownGuard
{
    /// <summary>
    /// Throws <see cref="PpdsException"/> with code
    /// <see cref="ErrorCodes.Safety.ShakedownActive"/> when a shakedown
    /// session is active. Returns normally when inactive. Internally cached
    /// for ≤5s between resolutions.
    /// </summary>
    /// <param name="operationDescription">
    /// Dot-delimited lowercase identifier for the blocked operation
    /// (e.g., <c>"plugintraces.delete"</c>). Embedded in the exception's
    /// <see cref="PpdsException.UserMessage"/> and <c>Context["operation"]</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="operationDescription"/> is <c>null</c>,
    /// empty, or whitespace. Indicates a bug in the calling service.
    /// </exception>
    /// <exception cref="PpdsException">
    /// Thrown with <see cref="PpdsException.ErrorCode"/> equal to
    /// <see cref="ErrorCodes.Safety.ShakedownActive"/> when shakedown is
    /// active.
    /// </exception>
    void EnsureCanMutate(string operationDescription);
}

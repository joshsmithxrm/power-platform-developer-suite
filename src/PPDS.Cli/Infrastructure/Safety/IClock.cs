namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Thin abstraction over <see cref="DateTimeOffset.UtcNow"/> to keep
/// <see cref="ShakedownGuard"/> unit-testable against deterministic time.
/// </summary>
public interface IClock
{
    /// <summary>Current UTC wall-clock time.</summary>
    DateTimeOffset UtcNow { get; }
}

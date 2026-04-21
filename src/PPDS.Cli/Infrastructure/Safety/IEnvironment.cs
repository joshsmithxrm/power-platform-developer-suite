namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Thin abstraction over environment-variable access so that
/// <see cref="ShakedownGuard"/> can be unit-tested without mutating
/// process-wide state.
/// </summary>
public interface IEnvironment
{
    /// <summary>
    /// Retrieves the value of the specified environment variable, or
    /// <c>null</c> when it is not defined.
    /// </summary>
    /// <param name="name">The name of the environment variable.</param>
    string? GetEnvironmentVariable(string name);
}

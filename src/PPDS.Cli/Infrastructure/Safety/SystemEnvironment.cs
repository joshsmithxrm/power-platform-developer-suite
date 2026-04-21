namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Default <see cref="IEnvironment"/> implementation backed by
/// <see cref="System.Environment"/>.
/// </summary>
public sealed class SystemEnvironment : IEnvironment
{
    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name)
        => System.Environment.GetEnvironmentVariable(name);
}

namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Default <see cref="IClock"/> implementation delegating to
/// <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

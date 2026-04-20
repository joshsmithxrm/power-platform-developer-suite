using PPDS.Cli.Infrastructure.Safety;

namespace PPDS.Cli.Tests.Infrastructure.Safety.Fakes;

/// <summary>
/// Settable-time <see cref="IClock"/> for deterministic tests.
/// </summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

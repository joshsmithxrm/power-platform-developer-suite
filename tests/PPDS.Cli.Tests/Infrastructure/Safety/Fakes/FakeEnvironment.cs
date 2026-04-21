using System.Collections.Generic;
using PPDS.Cli.Infrastructure.Safety;

namespace PPDS.Cli.Tests.Infrastructure.Safety.Fakes;

/// <summary>
/// Dictionary-backed <see cref="IEnvironment"/> for deterministic unit tests.
/// </summary>
internal sealed class FakeEnvironment : IEnvironment
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public string? this[string name]
    {
        get => _values.TryGetValue(name, out var v) ? v : null;
        set => _values[name] = value;
    }

    public string? GetEnvironmentVariable(string name)
        => _values.TryGetValue(name, out var v) ? v : null;
}

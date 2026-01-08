using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Auth.Pooling;
using PPDS.Dataverse.Pooling;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Adapts <see cref="ProfileConnectionSource"/> from PPDS.Auth to implement
/// <see cref="IConnectionSource"/> from PPDS.Dataverse.
/// </summary>
/// <remarks>
/// This adapter bridges the two packages without creating a circular dependency.
/// PPDS.Auth provides the profile-based connection source, PPDS.Dataverse provides
/// the pool interface, and PPDS.Mcp connects them.
/// </remarks>
internal sealed class ProfileConnectionSourceAdapter : IConnectionSource
{
    private readonly ProfileConnectionSource _source;

    /// <summary>
    /// Creates a new adapter wrapping the profile connection source.
    /// </summary>
    /// <param name="source">The profile connection source to wrap.</param>
    public ProfileConnectionSourceAdapter(ProfileConnectionSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <inheritdoc />
    public string Name => _source.Name;

    /// <inheritdoc />
    public int MaxPoolSize => _source.MaxPoolSize;

    /// <inheritdoc />
    public ServiceClient GetSeedClient() => _source.GetSeedClient();

    /// <inheritdoc />
    public void InvalidateSeed() => _source.InvalidateSeed();

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();
}

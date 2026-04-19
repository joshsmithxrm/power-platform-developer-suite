using System.Collections.Generic;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Test-only <see cref="IBrowserLauncher"/> that records every URL instead of
/// launching the OS browser. Preserves the scheme validation guard so behaviour
/// around invalid URLs still exercises the real code path.
/// </summary>
public sealed class NoOpBrowserLauncher : IBrowserLauncher
{
    private readonly List<string> _openedUrls = new();
    private readonly object _gate = new();

    /// <summary>
    /// URLs passed to <see cref="OpenUrl"/> in the order they were requested.
    /// </summary>
    public IReadOnlyList<string> OpenedUrls
    {
        get
        {
            lock (_gate)
            {
                return _openedUrls.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool OpenUrl(string url)
    {
        BrowserHelper.ValidateUrl(url);
        lock (_gate)
        {
            _openedUrls.Add(url);
        }
        return true;
    }
}

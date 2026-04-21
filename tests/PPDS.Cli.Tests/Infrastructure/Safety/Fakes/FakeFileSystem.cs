using System.Collections.Generic;
using System.IO;
using System.Threading;
using PPDS.Cli.Infrastructure.Safety;

namespace PPDS.Cli.Tests.Infrastructure.Safety.Fakes;

/// <summary>
/// In-memory <see cref="IFileSystem"/> for unit tests. Supports adding
/// files/directories, configuring a throw-on-open override for specific
/// paths (AC-12 locked-sentinel simulation), and exposes a stat counter so
/// tests can assert cache effectiveness (AC-11).
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _writeTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _openRedirects = new(StringComparer.OrdinalIgnoreCase);

    private int _fileExistsCallCount;

    /// <summary>
    /// Value returned from <see cref="GetCurrentDirectory"/>. Defaults to an
    /// empty string so tests that do not set it cannot accidentally resolve
    /// to any registered fake directory.
    /// </summary>
    public string CurrentDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Count of <see cref="FileExists"/> + <see cref="OpenRead"/> invocations
    /// against the sentinel — used by AC-11 to prove the cache suppresses
    /// redundant stats.
    /// </summary>
    public int StatCount => _fileExistsCallCount;

    public void AddDirectory(string path) => _directories.Add(path);

    public void AddFile(string path, string content)
        => AddFile(path, System.Text.Encoding.UTF8.GetBytes(content));

    public void AddFile(string path, byte[] content)
    {
        _files[path] = content;
        _writeTimes[path] = DateTimeOffset.UtcNow;
        // Ensure parent directory is registered so DirectoryExists works
        // naturally for callers that check containment.
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            _directories.Add(dir);
    }

    public void SetLastWriteTime(string path, DateTimeOffset timestamp)
        => _writeTimes[path] = timestamp;

    public void ThrowOnOpen(string path, Exception exception)
        => _openRedirects[path] = exception;

    public bool DirectoryExists(string path) => _directories.Contains(path);

    public bool FileExists(string path)
    {
        Interlocked.Increment(ref _fileExistsCallCount);
        return _files.ContainsKey(path);
    }

    public Stream OpenRead(string path)
    {
        Interlocked.Increment(ref _fileExistsCallCount);
        if (_openRedirects.TryGetValue(path, out var ex))
            throw ex;

        if (!_files.TryGetValue(path, out var bytes))
            throw new FileNotFoundException($"FakeFileSystem: {path} not found.", path);

        return new MemoryStream(bytes, writable: false);
    }

    public DateTimeOffset GetLastWriteTimeUtc(string path)
        => _writeTimes.TryGetValue(path, out var t) ? t : DateTimeOffset.MinValue;

    public string GetCurrentDirectory() => CurrentDirectory;
}

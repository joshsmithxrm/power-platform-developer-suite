namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Thin abstraction over the small subset of <see cref="System.IO"/> APIs
/// required by <see cref="ShakedownGuard"/>. Allows fake file systems in
/// unit tests and keeps the guard free of hidden IO coupling.
/// </summary>
public interface IFileSystem
{
    /// <summary>Returns <c>true</c> when the directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Returns <c>true</c> when the file exists.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Opens the file at <paramref name="path"/> for reading. Callers are
    /// responsible for disposing the returned stream.
    /// </summary>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the file cannot be opened (e.g., locked by another
    /// process). The guard catches this as part of its fail-open policy.
    /// </exception>
    Stream OpenRead(string path);

    /// <summary>
    /// Returns the UTC last-write time of the file at <paramref name="path"/>.
    /// </summary>
    DateTimeOffset GetLastWriteTimeUtc(string path);
}

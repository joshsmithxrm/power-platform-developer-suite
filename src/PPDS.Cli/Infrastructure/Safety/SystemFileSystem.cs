namespace PPDS.Cli.Infrastructure.Safety;

/// <summary>
/// Default <see cref="IFileSystem"/> implementation backed by
/// <see cref="System.IO"/>.
/// </summary>
public sealed class SystemFileSystem : IFileSystem
{
    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public Stream OpenRead(string path) => File.OpenRead(path);

    /// <inheritdoc />
    public DateTimeOffset GetLastWriteTimeUtc(string path)
        => new(File.GetLastWriteTimeUtc(path));
}

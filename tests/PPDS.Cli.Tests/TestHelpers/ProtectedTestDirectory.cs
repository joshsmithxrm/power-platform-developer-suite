namespace PPDS.Cli.Tests.TestHelpers;

/// <summary>
/// Creates per-test directories for fixtures that pass through the reference-cache trust check.
/// </summary>
/// <remarks>
/// The trust check validates every ancestor of a cache path and rejects one that another account can
/// modify. On Windows the per-user temp directory can carry such grants (local tooling can add a group
/// with Modify to it), which makes every test rooted there fail for a reason unrelated to the code under
/// test. Those fixtures are rooted under per-user application data instead, the same base the product
/// uses for its real cache. Elsewhere the temp directory is kept: the product explicitly accepts a
/// root-owned sticky ancestor such as /tmp, and the directory itself is created owner-only.
/// </remarks>
internal static class ProtectedTestDirectory
{
    private const string WindowsRootName = "PPDS.Tests";

    /// <summary>Creates a new, uniquely named directory and returns its full path.</summary>
    /// <param name="prefix">Leading part of the directory name, for example <c>ppds-trust-test-</c>.</param>
    internal static string Create(string prefix)
    {
        if (!OperatingSystem.IsWindows())
            return Directory.CreateTempSubdirectory(prefix).FullName;

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData) || !Path.IsPathFullyQualified(localApplicationData))
            return Directory.CreateTempSubdirectory(prefix).FullName;

        var path = Path.Combine(localApplicationData, WindowsRootName, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

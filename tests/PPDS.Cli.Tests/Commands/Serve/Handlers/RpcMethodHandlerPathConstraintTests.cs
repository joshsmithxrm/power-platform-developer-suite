using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Tests for the workspace-path constraint helper on <see cref="RpcMethodHandler"/>.
/// Covers G1 (file path constraint for RPC-exposed paths).
/// </summary>
public class RpcMethodHandlerPathConstraintTests
{
    [Fact]
    public void ResolveWorkspacePath_RelativePath_ResolvesUnderRoot()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var resolved = RpcMethodHandler.ResolveWorkspacePath("child.zip", "filePath", root);

        Assert.StartsWith(root, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("child.zip", resolved);
    }

    [Fact]
    public void ResolveWorkspacePath_NestedRelativePath_ResolvesUnderRoot()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var resolved = RpcMethodHandler.ResolveWorkspacePath(
            Path.Combine("sub", "deployment.json"),
            "filePath",
            root);

        Assert.StartsWith(root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveWorkspacePath_AbsolutePathOutsideRoot_Throws()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var outside = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\config\SAM"
            : "/etc/shadow";

        var ex = Assert.Throws<RpcException>(() =>
            RpcMethodHandler.ResolveWorkspacePath(outside, "filePath", root));

        Assert.Equal(ErrorCodes.Validation.PathOutsideWorkspace, ex.StructuredErrorCode);
    }

    [Fact]
    public void ResolveWorkspacePath_DotDotEscape_Throws()
    {
        // Build a child directory under TempPath so ".." walks OUT of it.
        var root = Path.Combine(Path.GetTempPath(), "ppds-test-root");
        Directory.CreateDirectory(root);

        try
        {
            var escape = Path.Combine("..", "..", "secrets.txt");

            var ex = Assert.Throws<RpcException>(() =>
                RpcMethodHandler.ResolveWorkspacePath(escape, "filePath", root));

            Assert.Equal(ErrorCodes.Validation.PathOutsideWorkspace, ex.StructuredErrorCode);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveWorkspacePath_PrefixMatchNotTreatedAsNested_Throws()
    {
        // "C:\\work" must not match "C:\\work-evil\\secret". Use absolute paths so
        // GetFullPath does not treat the input as root-relative.
        if (!OperatingSystem.IsWindows())
        {
            // Skip on non-Windows: POSIX FS already distinguishes via trailing separator.
            return;
        }

        var root = @"C:\ppds-workspace";
        var evil = @"C:\ppds-workspace-evil\leaked.zip";

        var ex = Assert.Throws<RpcException>(() =>
            RpcMethodHandler.ResolveWorkspacePath(evil, "filePath", root));

        Assert.Equal(ErrorCodes.Validation.PathOutsideWorkspace, ex.StructuredErrorCode);
    }

    [Fact]
    public void ResolveWorkspacePath_EmptyInput_ThrowsRequiredField()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var ex = Assert.Throws<RpcException>(() =>
            RpcMethodHandler.ResolveWorkspacePath("  ", "filePath", root));

        Assert.Equal(ErrorCodes.Validation.RequiredField, ex.StructuredErrorCode);
    }

    [Fact]
    public void ResolveWorkspacePath_PathEqualToRoot_AllowedAndReturnsRoot()
    {
        var root = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var resolved = RpcMethodHandler.ResolveWorkspacePath(root, "filePath", root);

        Assert.Equal(root, resolved, ignoreCase: OperatingSystem.IsWindows());
    }
}

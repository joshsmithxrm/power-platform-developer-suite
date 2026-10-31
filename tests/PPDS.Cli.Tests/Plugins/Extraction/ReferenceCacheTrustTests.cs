using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Extraction;

[Collection(nameof(ConsoleCaptureCollection))]
public sealed class ReferenceCacheTrustTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ppds-trust-test-").FullName;

    [Theory]
    [InlineData("base")]
    [InlineData("cache")]
    [InlineData("file")]
    [InlineData("lock")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void Prepare_UnsafePermissions_RejectsWithoutChangingContent(string position)
    {
        var root = Path.Combine(_root, "cache");
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root);
        Assert.NotNull(cache);
        var original = File.ReadAllBytes(Path.Combine(cache, "System.Runtime.dll"));
        var path = position switch
        {
            "base" => root, "cache" => cache, "lock" => cache + ".lock",
            _ => Path.Combine(cache, "System.Runtime.dll")
        };
        var restore = AllowOtherWrite(path);

        try
        {
            Assert.Null(AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(cache, "System.Runtime.dll")));
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-64")]
    public void GetReferenceDirectory_UnsafeStorage_WarnsAndPreservesContent()
    {
        var root = Path.Combine(_root, "blocked");
        File.WriteAllText(root, "leave this obstruction alone");
        var originalError = Console.Error;
        var originalOut = Console.Out;
        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        try
        {
            Console.SetError(stderr);
            Console.SetOut(stdout);
            Assert.Null(AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root));
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }
        Assert.Contains("Warning:", stderr.ToString());
        Assert.Contains(root, stderr.ToString());
        Assert.Empty(stdout.ToString());
        Assert.Equal("leave this obstruction alone", File.ReadAllText(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-61")]
    public void DefaultPath_UsesLocalApplicationData(bool trailingSeparator)
    {
        var data = Path.Combine(_root, "appdata");
        var actual = ReferenceCacheDirectory.PrepareDefault(data + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""));
        Assert.EndsWith(Path.Combine("PPDS", "cache", "net462-ref"), actual);
        File.WriteAllText(Path.Combine(actual, "location"), "private");
        Assert.Equal("private", File.ReadAllText(Path.Combine(data, "PPDS", "cache", "net462-ref", "location")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-64")]
    public void DefaultPath_InvalidStorage_Rejects(string path)
        => Assert.Throws<UnauthorizedAccessException>(() => ReferenceCacheDirectory.PrepareDefault(path));

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-65")]
    public void PrivateCache_RecoveryAndReuse()
    {
        var root = ReferenceCacheDirectory.PrepareDefault(Path.Combine(_root, "data"));
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root);
        Assert.NotNull(cache);
        var facade = Path.Combine(cache, "System.Runtime.dll");
        var original = File.ReadAllBytes(facade);
        File.Delete(facade);
        Assert.Equal(cache, AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root));
        Assert.Equal(original, File.ReadAllBytes(facade));
        var timestamp = File.GetLastWriteTimeUtc(facade);
        Assert.Equal(cache, AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(facade));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-65")]
    public async Task PrivateCache_WaitsForRepairLockBeforeTrustInspection()
    {
        var root = Path.Combine(_root, "cache");
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root);
        Assert.NotNull(cache);
        var facade = Path.Combine(cache, "System.Runtime.dll");
        var expected = File.ReadAllBytes(facade);
        var timestamp = File.GetLastWriteTimeUtc(facade);
        // Hold a transient, invalid tree state behind the cooperating writer's lock.
        // Readers must inspect the final tree after the writer finishes, not fail
        // midway through repair. Existing unsafe-storage tests cover permanent states.
        using var started = new ManualResetEventSlim();
        Task<string?> reader;
        bool completedWhileLocked;
        using (var writer = new FileStream(cache + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var restore = AllowOtherWrite(cache);
            reader = Task.Factory.StartNew(() =>
            {
                started.Set();
                return AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "Reader did not start.");
                await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(1)));
                completedWhileLocked = reader.IsCompleted;
            }
            finally
            {
                restore();
            }
        }

        var result = await reader.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(completedWhileLocked, "Reader inspected a tree while a repair held the lock.");
        Assert.Equal(cache, result);
        Assert.Equal(expected, File.ReadAllBytes(facade));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(facade));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("cache")]
    [InlineData("lock")]
    [InlineData("file")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-63")]
    public void Prepare_CacheLinks_RejectsWithoutChangingTarget(string position)
    {
        var root = ReferenceCacheDirectory.Prepare(Path.Combine(_root, "storage"));
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root)!;
        Assert.NotNull(cache);
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var marker = Path.Combine(target, "untouched");
        File.WriteAllText(marker, "preserve");
        var link = position switch
        {
            "base" => root, "cache" => cache, "lock" => cache + ".lock",
            _ => Path.Combine(cache, "System.Runtime.dll")
        };
        if (Directory.Exists(link)) Directory.Delete(link, true);
        else File.Delete(link);
        CreateDirectoryLink(link, target);
        try
        {
            Assert.Null(AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(root));
            Assert.Equal("preserve", File.ReadAllText(marker));
            Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-63")]
    public void Prepare_ProtectedAncestorLink_ResolvesToValidatedLocation()
    {
        var target = ReferenceCacheDirectory.Prepare(Path.Combine(_root, "destination"));
        var link = Path.Combine(_root, "ancestor");
        CreateDirectoryLink(link, target);
        try
        {
            var actual = ReferenceCacheDirectory.Prepare(Path.Combine(link, "cache"));
            Assert.Equal(Path.Combine(target, "cache"), actual);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-63")]
    public void Prepare_UnsafeIntermediateLink_RejectsSafeFinalTarget()
    {
        var target = ReferenceCacheDirectory.Prepare(Path.Combine(_root, "destination"));
        var intermediate = ReferenceCacheDirectory.Prepare(Path.Combine(_root, "intermediate"));
        var hop = Path.Combine(intermediate, "hop");
        var first = Path.Combine(_root, "first");
        CreateDirectoryLink(hop, target);
        CreateDirectoryLink(first, hop);
        var restore = AllowOtherWrite(intermediate);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ReferenceCacheDirectory.Prepare(Path.Combine(first, "cache")));
            Assert.False(Directory.Exists(Path.Combine(target, "cache")));
        }
        finally { restore(); Directory.Delete(first); Directory.Delete(hop); }
    }

    [WindowsCacheTheory]
    [SupportedOSPlatform("windows")]
    [InlineData("O:WD D:(A;;FA;;;WD)")]
    [InlineData("O:SY D:NO_ACCESS_CONTROL")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void WindowsAcl_UntrustedOwnerOrNullDacl_Rejects(string descriptor)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(descriptor.Replace(" ", ""));
        Assert.Throws<UnauthorizedAccessException>(() =>
            ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, identity.User!, false));
    }

    [WindowsCacheFact]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void WindowsAcl_CapabilitiesDoNotBypassAccountBoundary()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        foreach (var capability in new[] { "S-1-15-2-1", "S-1-15-3-1" })
        {
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(capability),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, user, false);
            security.SetOwner(new SecurityIdentifier(capability));
            Assert.Throws<UnauthorizedAccessException>(() =>
                ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, user, false));
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Write, AccessControlType.Allow));
            Assert.Throws<UnauthorizedAccessException>(() =>
                ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, user, false));
        }
    }

    [WindowsCacheTheory]
    [SupportedOSPlatform("windows")]
    [InlineData(0x10000000)] // GENERIC_ALL
    [InlineData(0x40000000)] // GENERIC_WRITE
    [InlineData((int)FileSystemRights.Delete)]
    [InlineData((int)FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData((int)FileSystemRights.ChangePermissions)]
    [InlineData((int)FileSystemRights.TakeOwnership)]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void WindowsAcl_UntrustedMutation_Rejects(int rights)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"O:{identity.User!.Value}D:(A;;0x{rights:x};;;WD)");
        Assert.Throws<UnauthorizedAccessException>(() =>
            ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, identity.User!, true));
    }

    [WindowsCacheFact]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void WindowsAcl_InheritableMutation_RejectsCacheButNotProtectedAncestor()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetOwner(identity.User!);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow));
        ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, identity.User!, true);
        Assert.Throws<UnauthorizedAccessException>(() =>
            ReferenceCacheDirectory.ValidateWindowsSecurity("fixture", security, identity.User!, false));
    }

    [MacCacheFact]
    [SupportedOSPlatform("macos")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void MacAcl_WriteGrantWithPrivateMode_Rejects()
    {
        var path = ReferenceCacheDirectory.Prepare(Path.Combine(_root, "acl"));
        Run("chmod", "+a", "everyone allow write,delete_child,file_inherit,directory_inherit", path);
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
            Assert.Throws<UnauthorizedAccessException>(() => ReferenceCacheDirectory.Prepare(path));
        }
        finally { Run("chmod", "-N", path); }
    }

    [MacCacheFact]
    [SupportedOSPlatform("macos")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void MacAcl_ReadAndDenyEntries_AreAccepted()
    {
        var path = ReferenceCacheDirectory.Prepare(Path.Combine(_root, "acl"));
        Run("chmod", "+a", "everyone deny delete", path);
        Run("chmod", "+a", "everyone allow read,search,readattr,readextattr,readsecurity", path);
        try { Assert.Equal(path, ReferenceCacheDirectory.Prepare(path)); }
        finally { Run("chmod", "-N", path); }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
            Run("cmd.exe", "/c", "mklink", "/J", link, target);
        else
            Directory.CreateSymbolicLink(link, target);
    }

    [UnixCacheFact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void UnixPermissions_RejectSpecialFilesAndMissingEntries()
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(Path.Combine(_root, "cache"))!;
        Assert.NotNull(cache);
        var pipe = Path.Combine(cache, "unexpected.pipe");
        Run("mkfifo", pipe);
        Assert.Throws<UnauthorizedAccessException>(() => ReferenceCacheDirectory.ValidateCache(cache));
        File.Delete(pipe);
        Assert.Throws<FileNotFoundException>(() => UnixCachePermissions.Validate(pipe, false));
    }

    [UnixCacheFact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-62")]
    public void UnixPermissions_NewStorageIsPrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(Path.Combine(_root, "cache"))!;
        Assert.NotNull(cache);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(cache));
        foreach (var file in Directory.GetFiles(cache).Append(cache + ".lock"))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    private static Action AllowOtherWrite(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = Directory.Exists(path);
            FileSystemSecurity original = directory
                ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            FileSystemSecurity changed = directory
                ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Write, AccessControlType.Allow));
            if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)changed);
            else new FileInfo(path).SetAccessControl((FileSecurity)changed);
            return () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                // SetAccessControl persists only modified sections. Mark the saved
                // DACL as modified so restoration actually removes the test grant.
                original.SetSecurityDescriptorBinaryForm(
                    original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)original);
                else new FileInfo(path).SetAccessControl((FileSecurity)original);
            };
        }
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.OtherWrite);
        return () => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); };
    }

    private static void Run(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("Fixture command timed out.");
        }
        Assert.True(process.ExitCode == 0, $"Fixture command failed: {output.GetAwaiter().GetResult()} {error.GetAwaiter().GetResult()}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
internal sealed class WindowsCacheFactAttribute : FactAttribute
{
    public WindowsCacheFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows ACL contract"; }
}
internal sealed class WindowsCacheTheoryAttribute : TheoryAttribute
{
    public WindowsCacheTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows ACL contract"; }
}
internal sealed class MacCacheFactAttribute : FactAttribute
{
    public MacCacheFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "macOS extended ACL contract"; }
}
internal sealed class UnixCacheFactAttribute : FactAttribute
{
    public UnixCacheFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix ownership/permission contract"; }
}

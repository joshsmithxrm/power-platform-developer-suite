using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PPDS.Cli.Plugins.Extraction;

/// <summary>Establishes the OS-account boundary before reference-cache I/O.</summary>
internal static class ReferenceCacheDirectory
{
    internal static string PrepareDefault(string? localApplicationData = null)
    {
        var data = localApplicationData ?? Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        RequireAbsolutePath(data);
        var directory = ResolveAncestor(Path.GetFullPath(data));
        ValidateEntry(directory, ancestor: false);
        foreach (var name in new[] { "PPDS", "cache", "net462-ref" })
            directory = CreatePrivateDirectory(Path.Combine(directory, name));
        return directory;
    }

    // The override is internal and used only by isolated filesystem tests.
    internal static string Prepare(string directory)
    {
        RequireAbsolutePath(directory);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw Unsafe(directory, "a filesystem root cannot be a cache directory");
        return CreatePrivateDirectory(Path.Combine(ResolveAncestor(parent), Path.GetFileName(fullPath)));
    }

    internal static void ValidateCache(string directory)
    {
        ValidateTreeIfPresent(directory);
        ValidateTreeIfPresent(directory + ".lock");
    }

    private static void RequireAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw Unsafe(path, "local application data must be a nonempty absolute path");
        if (OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
            throw Unsafe(path, "the reference cache requires local user storage");
    }

    private static string ResolveAncestor(string directory, int remaining = 64)
    {
        directory = Path.TrimEndingDirectorySeparator(directory);
        if (remaining == 0)
            throw Unsafe(directory, "too many ancestor directories or links");

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
        if (parent != null)
            directory = Path.Combine(ResolveAncestor(parent, remaining - 1), Path.GetFileName(directory));

        var attributes = AttributesIfPresent(directory);
        if (attributes == null)
            return CreatePrivateDirectory(directory);

        // Validate the original traversal, including the link's protected parent, before
        // resolving it. Only the separately validated destination is used for later I/O.
        ValidateEntry(directory, ancestor: true);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Resolve one hop at a time: a safe final target does not make an intermediate
            // link in another account's writable directory safe to traverse.
            var target = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: false)
                ?? throw Unsafe(directory, "cannot resolve ancestor link");
            return ResolveAncestor(target.FullName, remaining - 1);
        }
        if ((attributes & FileAttributes.Directory) == 0)
            throw Unsafe(directory, "an ancestor is not a directory");
        return directory;
    }

    internal static string CreatePrivateDirectory(string directory)
    {
        if (AttributesIfPresent(directory) == null)
        {
            if (OperatingSystem.IsWindows())
                CreateWindowsDirectory(directory);
            else
                Directory.CreateDirectory(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        ValidateEntry(directory, ancestor: false);
        if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
            throw Unsafe(directory, "the cache path is not a directory");
        return directory;
    }

    private static void ValidateTreeIfPresent(string path)
    {
        try
        {
            var attributes = AttributesIfPresent(path);
            if (attributes == null)
                return;
            ValidateEntry(path, ancestor: false);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    ValidateTreeIfPresent(child);
            }
        }
        // Cooperating repairs can replace an incomplete cache while we inspect it.
        // Missing entries are handled by completeness validation, not a trust failure.
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static FileAttributes? AttributesIfPresent(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void ValidateEntry(string path, bool ancestor)
    {
        var attributes = File.GetAttributes(path);
        if (!ancestor && (attributes & FileAttributes.ReparsePoint) != 0)
            throw Unsafe(path, "links/reparse points are not allowed in reference-cache storage");
        if (OperatingSystem.IsWindows())
            ValidateWindows(path, attributes, ancestor);
        else
            UnixCachePermissions.Validate(path, ancestor);
    }

    internal static UnauthorizedAccessException Unsafe(string path, string reason)
        => new($"Unsafe reference-cache path '{path}': {reason}. Use protected local user storage.");

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw Unsafe(path, "current Windows user SID is unavailable");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindows(string path, FileAttributes attributes, bool ancestor)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw Unsafe(path, "current Windows user SID is unavailable");
        FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateWindowsSecurity(path, security, user, ancestor);
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateWindowsSecurity(
        string path, FileSystemSecurity security, SecurityIdentifier user, bool ancestor)
    {
        bool Trusted(IdentityReference? owner) => owner is SecurityIdentifier sid &&
            (sid == user || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || (ancestor && sid.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")); // TrustedInstaller

        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))))
            throw Unsafe(path, "owner is not the current user or a trusted OS principal");
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl == null)
            throw Unsafe(path, "a null DACL grants unrestricted access");

        // Include generic rights: raw ACLs may retain GENERIC_ALL/GENERIC_WRITE rather
        // than the mapped FileSystemRights bits. Unknown grants are conservatively unsafe.
        uint permitted = (uint)(FileSystemRights.ReadAndExecute | FileSystemRights.ReadPermissions
            | FileSystemRights.Synchronize) | 0xA0000000; // GENERIC_READ / GENERIC_EXECUTE
        if (ancestor)
            permitted |= (uint)(FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories);

        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce rule)
                throw Unsafe(path, "an unknown ACL entry prevents permission validation");
            // Only ancestors' inherit-only grants can be ignored: new cache directories
            // get protected DACLs. Cache-owned inherited grants also affect staging/files.
            if (ancestor && (rule.AceFlags & AceFlags.InheritOnly) != 0)
                continue;
            if (rule.AceQualifier == AceQualifier.AccessDenied)
                continue;
            // AppContainer package/capability grants are an additional access check, not
            // ordinary account grants. Both halves must succeed; another user's ordinary
            // write grant is still rejected below. These SIDs are never trusted owners.
            var sid = rule.SecurityIdentifier.Value;
            if (rule.AceQualifier == AceQualifier.AccessAllowed
                && (sid.StartsWith("S-1-15-2-", StringComparison.Ordinal)
                    || sid.StartsWith("S-1-15-3-", StringComparison.Ordinal)))
                continue;
            if (rule.AceQualifier != AceQualifier.AccessAllowed
                || ((unchecked((uint)rule.AccessMask) & ~permitted) != 0 && !Trusted(rule.SecurityIdentifier)))
            {
                throw Unsafe(path, "an untrusted principal has mutation permissions");
            }
        }
    }
}

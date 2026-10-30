using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PPDS.Cli.Plugins.Extraction;

/// <summary>Reads native ownership and macOS ACLs; Unix mode bits alone are insufficient.</summary>
internal static class UnixCachePermissions
{
    internal static void Validate(string path, bool ancestor)
    {
        uint owner, mode, user;
        int result;
        if (OperatingSystem.IsMacOS())
        {
            MacStat status;
            result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? LStatMacX64(path, out status) : LStatMacArm64(path, out status);
            owner = status.Owner; mode = status.Mode; user = GetMacUser();
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            result = LStatLinuxX64(1, path, out var status); // _STAT_VER_LINUX on x86_64
            owner = status.Owner; mode = status.Mode; user = GetLinuxUser();
        }
        else if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            result = LStatLinuxArm64(path, out var status);
            owner = status.Owner; mode = status.Mode; user = GetLinuxUser();
        }
        else
        {
            throw ReferenceCacheDirectory.Unsafe(path, "ownership checks are unavailable on this platform");
        }
        if (result != 0)
            throw NativeError(path);
        if (owner != user && !(ancestor && owner == 0))
            throw ReferenceCacheDirectory.Unsafe(path, "directory/file belongs to another OS account");

        const uint typeMask = 0xF000, directory = 0x4000, file = 0x8000, link = 0xA000;
        var kind = mode & typeMask;
        if (kind != directory && kind != file && !(ancestor && kind == link))
            throw ReferenceCacheDirectory.Unsafe(path, "unexpected filesystem object type");
        // Root-owned sticky ancestors preserve ownership of each existing child (/tmp).
        var stickyRoot = ancestor && owner == 0 && kind == directory && (mode & 0x200) != 0;
        if (kind != link && (mode & 0x12) != 0 && !stickyRoot) // group/other write
            throw ReferenceCacheDirectory.Unsafe(path, "group/other accounts can write this path");
        if (OperatingSystem.IsMacOS())
            ValidateMacAcl(path);
    }

    private static void ValidateMacAcl(string path)
    {
        const int extendedAcl = 0x100, allow = 1, deny = 2;
        const ulong readOnlyPermissions = 0x100A8A; // read/list/search, attributes, security, synchronize
        var acl = GetMacAcl(path, extendedAcl);
        if (acl == IntPtr.Zero)
        {
            if (Marshal.GetLastPInvokeError() == 2) // No extended ACL (ENOENT), after lstat succeeded.
                return;
            throw NativeError(path);
        }
        try
        {
            var selector = 0; // ACL_FIRST_ENTRY; ACL_NEXT_ENTRY is -1 on Darwin.
            while (GetMacAclEntry(acl, selector, out var entry) == 0)
            {
                selector = -1;
                if (GetMacAclTag(entry, out var tag) != 0 || GetMacAclMask(entry, out var permissions) != 0)
                    throw NativeError(path);
                if (tag != deny && (tag != allow || (permissions & ~readOnlyPermissions) != 0))
                    throw ReferenceCacheDirectory.Unsafe(path, "extended ACL grants mutation or has unknown permissions");
            }
            if (Marshal.GetLastPInvokeError() != 22) // EINVAL denotes exhausted Darwin ACL iterator.
                throw NativeError(path);
        }
        finally { FreeMacAcl(acl); }
    }

    private static Exception NativeError(string path)
    {
        var error = Marshal.GetLastPInvokeError();
        if (error == 2)
            return new FileNotFoundException("Reference-cache entry disappeared during inspection.", path);
        return new IOException($"Cannot inspect reference-cache permissions for '{path}': {new Win32Exception(error).Message}");
    }

    // ABI layouts: glibc x86_64 bits/struct_stat.h, Linux asm-generic/stat.h,
    // and Apple's __DARWIN_STRUCT_STAT64 in sys/stat.h. Only needed fields are exposed;
    // full native buffer sizes and the Darwin inode64 symbol must stay in sync.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxX64Stat { [FieldOffset(24)] public uint Mode; [FieldOffset(28)] public uint Owner; }
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct LinuxArm64Stat { [FieldOffset(16)] public uint Mode; [FieldOffset(24)] public uint Owner; }
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStat { [FieldOffset(4)] public ushort Mode; [FieldOffset(16)] public uint Owner; }

    private const string Libc = "libc.so.6", LibSystem = "/usr/lib/libSystem.B.dylib";
    // glibc before 2.33 exposes __lxstat rather than lstat on x86_64. Version 1 is
    // the Linux x86_64 struct stat ABI on both old and current glibc releases.
    [DllImport(Libc, EntryPoint = "__lxstat", SetLastError = true)]
    private static extern int LStatLinuxX64(int version, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out LinuxX64Stat status);
    [DllImport(Libc, EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStatLinuxArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out LinuxArm64Stat status);
    [DllImport(LibSystem, EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int LStatMacX64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat status);
    [DllImport(LibSystem, EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStatMacArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat status);
    [DllImport(Libc, EntryPoint = "geteuid")]
    private static extern uint GetLinuxUser();
    [DllImport(LibSystem, EntryPoint = "geteuid")]
    private static extern uint GetMacUser();
    [DllImport(LibSystem, EntryPoint = "acl_get_link_np", SetLastError = true)]
    private static extern IntPtr GetMacAcl([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int type);
    [DllImport(LibSystem, EntryPoint = "acl_get_entry", SetLastError = true)]
    private static extern int GetMacAclEntry(IntPtr acl, int selector, out IntPtr entry);
    [DllImport(LibSystem, EntryPoint = "acl_get_tag_type", SetLastError = true)]
    private static extern int GetMacAclTag(IntPtr entry, out int tag);
    [DllImport(LibSystem, EntryPoint = "acl_get_permset_mask_np", SetLastError = true)]
    private static extern int GetMacAclMask(IntPtr entry, out ulong permissions);
    [DllImport(LibSystem, EntryPoint = "acl_free")]
    private static extern int FreeMacAcl(IntPtr acl);
}

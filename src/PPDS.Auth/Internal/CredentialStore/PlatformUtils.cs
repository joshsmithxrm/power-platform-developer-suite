// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: reimplemented as a minimal dependency-free helper rather than
// copied from upstream PlatformUtils.cs (which drags in many unrelated GCM
// abstractions). Provides only the OS-detection + guard methods referenced by
// vendored OS backends, using System.Runtime.InteropServices.RuntimeInformation.
using System;
using System.Runtime.InteropServices;

namespace PPDS.Auth.Internal.CredentialStore
{
    internal static class PlatformUtils
    {
        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public static bool IsPosix() => IsMacOS() || IsLinux();

        public static void EnsureWindows()
        {
            if (!IsWindows())
            {
                throw new PlatformNotSupportedException("Operation requires Windows.");
            }
        }

        public static void EnsureMacOS()
        {
            if (!IsMacOS())
            {
                throw new PlatformNotSupportedException("Operation requires macOS.");
            }
        }

        public static void EnsureLinux()
        {
            if (!IsLinux())
            {
                throw new PlatformNotSupportedException("Operation requires Linux.");
            }
        }
    }
}

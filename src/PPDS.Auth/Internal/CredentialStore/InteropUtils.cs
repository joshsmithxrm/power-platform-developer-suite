// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; no behavioral change to OS storage semantics.
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace PPDS.Auth.Internal.CredentialStore
{
    internal static class InteropUtils
    {
        public static byte[] ToByteArray(IntPtr ptr, long count)
        {
            var destination = new byte[count];
            Marshal.Copy(ptr, destination, 0, destination.Length);
            return destination;
        }

        public static bool AreEqual(byte[] bytes, IntPtr ptr, uint length)
        {
            if (bytes.Length == 0 && (ptr == IntPtr.Zero || length == 0))
            {
                return true;
            }

            if (bytes.Length != length)
            {
                return false;
            }

            byte[] ptrBytes = ToByteArray(ptr, length);
            return bytes.SequenceEqual(ptrBytes);
        }
    }
}

// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; no behavioral change to OS storage semantics.
using System;
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

        // PPDS-Modification: compare byte-by-byte via Marshal.ReadByte rather than
        // copying the unmanaged buffer into a managed byte[] (which would leave a
        // plaintext copy of the secret in GC memory past the call). Constitution S3.
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

            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != Marshal.ReadByte(ptr, i))
                {
                    return false;
                }
            }
            return true;
        }
    }
}

// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore.MacOS.Native;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; no behavioral change to OS storage semantics.
using System;
using System.Runtime.InteropServices;

namespace PPDS.Auth.Internal.CredentialStore.MacOS.Native
{
    internal static class LibSystem
    {
        private const string LibSystemLib = "/System/Library/Frameworks/System.framework/System";

        [DllImport(LibSystemLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dlopen(string name, int flags);

        [DllImport(LibSystemLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr dlsym(IntPtr handle, string symbol);

        public static IntPtr GetGlobal(IntPtr handle, string symbol)
        {
            IntPtr ptr = dlsym(handle, symbol);
            return Marshal.PtrToStructure<IntPtr>(ptr);
        }
    }
}

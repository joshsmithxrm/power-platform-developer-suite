// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; trimmed to the three methods used by vendored code
// (NotNull<T>, NotNullOrEmpty, NotNullOrWhiteSpace).
using System;

namespace PPDS.Auth.Internal.CredentialStore
{
    internal static class EnsureArgument
    {
        public static void NotNull<T>(T arg, string name)
        {
            if (arg is null)
            {
                throw new ArgumentNullException(name);
            }
        }

        public static void NotNullOrEmpty(string arg, string name)
        {
            NotNull(arg, name);

            if (string.IsNullOrEmpty(arg))
            {
                throw new ArgumentException("Argument cannot be empty.", name);
            }
        }

        public static void NotNullOrWhiteSpace(string arg, string name)
        {
            NotNull(arg, name);

            if (string.IsNullOrWhiteSpace(arg))
            {
                throw new ArgumentException("Argument cannot be empty or white space.", name);
            }
        }
    }
}

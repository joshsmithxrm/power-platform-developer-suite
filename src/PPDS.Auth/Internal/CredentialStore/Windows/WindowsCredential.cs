// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore.Windows;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; no behavioral change to OS storage semantics.
namespace PPDS.Auth.Internal.CredentialStore.Windows
{
    internal class WindowsCredential : ICredential
    {
        public WindowsCredential(string service, string userName, string password, string targetName)
        {
            Service = service;
            UserName = userName;
            Password = password;
            TargetName = targetName;
        }

        public string Service { get; }

        public string UserName { get; }

        public string Password { get; }

        public string TargetName { get; }

        string ICredential.Account => UserName;
    }
}

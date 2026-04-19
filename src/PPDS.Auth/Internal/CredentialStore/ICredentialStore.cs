// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; no behavioral change to OS storage semantics.
using System.Collections.Generic;

namespace PPDS.Auth.Internal.CredentialStore
{
    /// <summary>
    /// Represents a secure storage location for <see cref="ICredential"/>s.
    /// </summary>
    internal interface ICredentialStore
    {
        /// <summary>
        /// Get all accounts from the store for the given service.
        /// </summary>
        /// <param name="service">Name of the service to match against. Use null to match all values.</param>
        /// <returns>All accounts that match the query.</returns>
        IList<string> GetAccounts(string service);

        /// <summary>
        /// Get the first credential from the store that matches the given query.
        /// </summary>
        /// <param name="service">Name of the service to match against. Use null to match all values.</param>
        /// <param name="account">Account name to match against. Use null to match all values.</param>
        /// <returns>First matching credential or null if none are found.</returns>
        ICredential? Get(string service, string account);

        /// <summary>
        /// Add or update credential in the store with the specified key.
        /// </summary>
        /// <param name="service">Name of the service this credential is for. Use null to match all values.</param>
        /// <param name="account">Account associated with this credential. Use null to match all values.</param>
        /// <param name="secret">Secret value to store.</param>
        void AddOrUpdate(string service, string account, string secret);

        /// <summary>
        /// Delete credential from the store that matches the given query.
        /// </summary>
        /// <param name="service">Name of the service to match against. Use null to match all values.</param>
        /// <param name="account">Account name to match against. Use null to match all values.</param>
        /// <returns>True if the credential was deleted, false otherwise.</returns>
        bool Remove(string service, string account);
    }
}

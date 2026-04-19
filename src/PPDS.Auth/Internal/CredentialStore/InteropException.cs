// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore;
// internal GCM abstractions (ITrace/ITrace2, ISessionManager, IEnvironment,
// IFileSystem) dropped or replaced with direct System.* calls; visibility
// lowered to `internal`; no behavioral change to OS storage semantics.
using System;
using System.ComponentModel;
using System.Diagnostics;

namespace PPDS.Auth.Internal.CredentialStore
{
    /// <summary>
    /// An unexpected error occurred in interop-code.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    internal class InteropException : Exception
    {
        public InteropException()
            : base() { }

        public InteropException(string message, int errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public InteropException(string message, int errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }

        public InteropException(string message, Win32Exception w32Exception)
            : base(message, w32Exception)
        {
            ErrorCode = w32Exception.NativeErrorCode;
        }

        /// <summary>
        /// Native error code.
        /// </summary>
        public int ErrorCode { get; }

        private string DebuggerDisplay => $"{Message} [0x{ErrorCode:x}]";
    }
}

// This file is PPDS-authored (not vendored). It replaces the upstream
// GitCredentialManager.CredentialStore factory with a minimal, dependency-free
// dispatcher for the three OS backends vendored in this directory.
using System;
using PPDS.Auth.Internal.CredentialStore.Linux;
using PPDS.Auth.Internal.CredentialStore.MacOS;
using PPDS.Auth.Internal.CredentialStore.Windows;

namespace PPDS.Auth.Internal.CredentialStore
{
    internal static class CredentialManager
    {
        /// <summary>
        /// Creates an <see cref="ICredentialStore"/> appropriate for the current OS.
        /// Mirrors the public surface of Devlooped.CredentialManager's CredentialManager.Create(...).
        /// On Linux, plaintext fallback requires BOTH <paramref name="allowPlaintextFallback"/>
        /// to be <c>true</c> AND the <c>GCM_CREDENTIAL_STORE=plaintext</c> environment variable
        /// to be set — this double-gate prevents accidental activation.
        /// </summary>
        /// <param name="namespace">Credential namespace (used as prefix for service names).</param>
        /// <param name="allowPlaintextFallback">
        /// Caller opt-in for Linux plaintext fallback. Must be combined with the
        /// <c>GCM_CREDENTIAL_STORE=plaintext</c> environment variable to take effect.
        /// </param>
        public static ICredentialStore Create(string @namespace, bool allowPlaintextFallback = false)
        {
            EnsureArgument.NotNullOrWhiteSpace(@namespace, nameof(@namespace));

            if (PlatformUtils.IsWindows())
            {
                return new WindowsCredentialManager(@namespace);
            }

            if (PlatformUtils.IsMacOS())
            {
                return new MacOSKeychain(@namespace);
            }

            if (PlatformUtils.IsLinux())
            {
                var backend = Environment.GetEnvironmentVariable("GCM_CREDENTIAL_STORE");
                bool envRequests = string.Equals(backend, "plaintext", StringComparison.OrdinalIgnoreCase);
                if (envRequests && allowPlaintextFallback)
                {
                    return new PlaintextCredentialStore(@namespace);
                }
                return new SecretServiceCollection(@namespace);
            }

            throw new PlatformNotSupportedException(
                $"Credential storage is not supported on the current OS.");
        }
    }
}

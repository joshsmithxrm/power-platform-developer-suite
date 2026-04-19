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
        /// Honors GCM_CREDENTIAL_STORE=plaintext on Linux for headless CI fallback.
        /// </summary>
        /// <param name="namespace">Credential namespace (used as prefix for service names).</param>
        public static ICredentialStore Create(string @namespace)
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
                if (string.Equals(backend, "plaintext", StringComparison.OrdinalIgnoreCase))
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

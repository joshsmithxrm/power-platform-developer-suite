// This file is PPDS-authored (not vendored). It provides the Linux plaintext
// credential-store fallback (activated via GCM_CREDENTIAL_STORE=plaintext)
// without depending on upstream's IFileSystem abstraction. The on-disk format
// matches upstream enough for headless CI: one file per credential under
// ~/.gcm/store/<service-slug>/<account>.credential, each file containing two
// lines: the secret (line 1) and the account identifier (line 2).
// Since the use case is CI-only where the store is ephemeral, the exact
// on-disk format need not be cross-compatible with any other implementation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PPDS.Auth.Internal.CredentialStore.Linux
{
    internal sealed class PlaintextCredentialStore : ICredentialStore
    {
        private const string CredentialFileExtension = ".credential";

        private readonly string _namespace;
        private readonly string _storeRoot;

        public PlaintextCredentialStore(string @namespace)
            : this(@namespace, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gcm", "store"))
        {
        }

        internal PlaintextCredentialStore(string @namespace, string storeRoot)
        {
            EnsureArgument.NotNullOrWhiteSpace(@namespace, nameof(@namespace));
            EnsureArgument.NotNullOrWhiteSpace(storeRoot, nameof(storeRoot));
            _namespace = @namespace;
            _storeRoot = storeRoot;
        }

        public IList<string> GetAccounts(string service)
        {
            return Enumerate(service, null).Select(x => x.Account).Distinct().ToList();
        }

        public ICredential? Get(string service, string account)
        {
            return Enumerate(service, account).FirstOrDefault();
        }

        public void AddOrUpdate(string service, string account, string secret)
        {
            EnsureArgument.NotNullOrWhiteSpace(service, nameof(service));

            Directory.CreateDirectory(_storeRoot);
            ClampDirectoryPermissions(_storeRoot);
            string serviceDir = Path.Combine(_storeRoot, Slug(CreateServiceName(service)));
            Directory.CreateDirectory(serviceDir);
            ClampDirectoryPermissions(serviceDir);

            string filePath = Path.Combine(serviceDir, $"{Slug(account ?? string.Empty)}{CredentialFileExtension}");
            // Two-line format: secret, then account identifier (for read-back).
            File.WriteAllText(filePath, secret + "\n" + (account ?? string.Empty) + "\n", Encoding.UTF8);
            ClampFilePermissions(filePath);
        }

        /// <summary>
        /// Clamp directory permissions to 0700 on Unix. No-op on Windows.
        /// Defense-in-depth against inherited umask 0022 which would leave the
        /// directory group/world-readable on shared hosts.
        /// </summary>
        private static void ClampDirectoryPermissions(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>
        /// Clamp file permissions to 0600 on Unix. No-op on Windows.
        /// </summary>
        private static void ClampFilePermissions(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public bool Remove(string service, string account)
        {
            var existing = Enumerate(service, account).FirstOrDefault();
            if (existing is null) return false;
            File.Delete(existing.FilePath);
            return true;
        }

        private IEnumerable<FileCred> Enumerate(string service, string? account)
        {
            if (!Directory.Exists(_storeRoot)) yield break;

            string? filterDir = string.IsNullOrWhiteSpace(service)
                ? null
                : Slug(CreateServiceName(service));

            foreach (var dir in Directory.EnumerateDirectories(_storeRoot))
            {
                if (filterDir is not null && !Path.GetFileName(dir).Equals(filterDir, StringComparison.Ordinal)) continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*" + CredentialFileExtension))
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                    catch { continue; }
                    if (lines.Length == 0) continue;
                    string storedSecret = lines[0];
                    string storedAccount = lines.Length > 1 ? lines[1] : string.Empty;
                    if (!string.IsNullOrWhiteSpace(account) &&
                        !StringComparer.Ordinal.Equals(account, storedAccount))
                    {
                        continue;
                    }
                    yield return new FileCred(service, storedAccount, storedSecret, file);
                }
            }
        }

        private string CreateServiceName(string service)
        {
            return string.IsNullOrWhiteSpace(_namespace) ? service : $"{_namespace}:{service}";
        }

        private static string Slug(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
            }
            return sb.ToString();
        }

        private sealed record FileCred(string Service, string Account, string Password, string FilePath) : ICredential
        {
            string ICredential.Account => Account;
            string ICredential.Password => Password;
        }
    }
}

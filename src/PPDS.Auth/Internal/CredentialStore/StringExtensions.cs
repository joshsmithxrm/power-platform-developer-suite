// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// Original source: Copyright (c) GitHub, Inc. and contributors. Licensed under MIT.
// See THIRD_PARTY_NOTICES.md for attribution.
// Modifications: namespace renamed to PPDS.Auth.Internal.CredentialStore;
// visibility lowered to `internal`; trimmed to the two TrimUntilIndexOf
// methods used by vendored code (character and string overloads).
using System;

namespace PPDS.Auth.Internal.CredentialStore
{
    internal static class StringExtensions
    {
        /// <summary>
        /// Trim all characters at the start of the string until the first index of the given character,
        /// also removing the indexed character.
        /// </summary>
        /// <param name="str">String to trim.</param>
        /// <param name="c">Character to locate the index of.</param>
        /// <returns>Trimmed string.</returns>
        public static string TrimUntilIndexOf(this string str, char c)
        {
            EnsureArgument.NotNull(str, nameof(str));

            int first = str.IndexOf(c);
            if (first > -1)
            {
                return str.Substring(first + 1, str.Length - first - 1);
            }

            return str;
        }

        /// <summary>
        /// Trim all characters at the start of the string until the first index of the given string,
        /// also removing the indexed character.
        /// </summary>
        /// <param name="str">String to trim.</param>
        /// <param name="value">String to locate the index of.</param>
        /// <param name="comparisonType">Comparison rule for locating the string.</param>
        /// <returns>Trimmed string.</returns>
        public static string TrimUntilIndexOf(this string str, string value, StringComparison comparisonType = StringComparison.Ordinal)
        {
            EnsureArgument.NotNull(str, nameof(str));

            int first = str.IndexOf(value, comparisonType);
            if (first > -1)
            {
                return str.Substring(first + value.Length, str.Length - first - value.Length);
            }

            return str;
        }
    }
}

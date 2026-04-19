using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Source-level regression guards for Workstream A5 (username hashing in
/// debug logs) and A6 (redaction of exception messages that may embed
/// connection strings).
/// </summary>
/// <remarks>
/// These are structural asserts — they check the C# source files, not
/// runtime behaviour, because the unsafe patterns we want to ban are
/// easy to reintroduce accidentally during refactors.
/// </remarks>
public class CredentialProviderLoggingGuardTests
{
    private static string CredentialsDir
    {
        get
        {
            var asm = typeof(CredentialProviderLoggingGuardTests).Assembly;
            var baseDir = Path.GetDirectoryName(asm.Location)!;
            return Path.GetFullPath(Path.Combine(
                baseDir, "..", "..", "..", "..", "..",
                "src", "PPDS.Auth", "Credentials"));
        }
    }

    private static bool SourceAvailable() => Directory.Exists(CredentialsDir);

    [Fact]
    public void AuthDebugLog_DoesNotLogRawUsername()
    {
        if (!SourceAvailable())
        {
            return;
        }

        // Disallowed: AuthDebugLog.WriteLine("... {x.Account.Username}") or
        // AuthDebugLog.WriteLine("... {account.Username}") without
        // LogIdentityHelper.HashIdentifier on the same line.
        var pattern = new Regex(
            @"AuthDebugLog\.WriteLine\([^)]*\{[^}]*\.?Account\??\.Username[^}]*\}",
            RegexOptions.Compiled);

        var offenders = (
            from path in Directory.EnumerateFiles(CredentialsDir, "*.cs")
            from line in File.ReadAllLines(path).Select((text, idx) => (text, idx))
            where pattern.IsMatch(line.text)
            where !line.text.Contains("HashIdentifier")
            select $"{Path.GetFileName(path)}:{line.idx + 1}: {line.text.Trim()}"
        ).ToList();

        offenders.Should().BeEmpty(
            "credential providers must hash usernames in debug logs via LogIdentityHelper.HashIdentifier");
    }

    [Fact]
    public void AuthDebugLog_DoesNotLogBareAccountUsername()
    {
        if (!SourceAvailable())
        {
            return;
        }

        // Also catch "Found account for silent auth: {account.Username}" style
        // where "account" is a local variable, not Result.Account.
        var pattern = new Regex(
            @"AuthDebugLog\.WriteLine\([^)]*\{account\.Username\}",
            RegexOptions.Compiled);

        var offenders = (
            from path in Directory.EnumerateFiles(CredentialsDir, "*.cs")
            from line in File.ReadAllLines(path).Select((text, idx) => (text, idx))
            where pattern.IsMatch(line.text)
            select $"{Path.GetFileName(path)}:{line.idx + 1}: {line.text.Trim()}"
        ).ToList();

        offenders.Should().BeEmpty(
            "credential providers must hash usernames in debug logs via LogIdentityHelper.HashIdentifier");
    }

    [Fact]
    public void NewAuthenticationException_RedactsExceptionMessage()
    {
        if (!SourceAvailable())
        {
            return;
        }

        // Disallowed pattern: new AuthenticationException( ... {ex.Message} ... )
        // without SensitiveValueRedactor.Redact somewhere in the same call.
        var callPattern = new Regex(
            @"new\s+AuthenticationException\s*\((?<body>[^;]*?)\);",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var offenders = new System.Collections.Generic.List<string>();

        foreach (var path in Directory.EnumerateFiles(CredentialsDir, "*.cs"))
        {
            // Skip files that intentionally don't catch exceptions (e.g. the
            // exception class itself).
            var file = Path.GetFileName(path);
            if (file == "AuthenticationException.cs")
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match m in callPattern.Matches(text))
            {
                var body = m.Groups["body"].Value;
                if (body.Contains("ex.Message") && !body.Contains("SensitiveValueRedactor"))
                {
                    offenders.Add($"{file}: {body.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "credential providers must redact ex.Message via SensitiveValueRedactor.Redact before wrapping");
    }
}

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests;

/// <summary>
/// Regression tests for FINDING #7 (v1.0.0 shakedown) — four auth / token-cache bugs:
///   Bug A — authority mismatch between GlobalDiscoveryService and InteractiveBrowserCredentialProvider
///   Bug B — memory-visibility gap on _cachedResult / _cachedResultUrl
///   Bug C — AuthenticationOutput writing to stdout instead of stderr
///   Bug D — VerifyPersistence running on every CreateAndRegisterCacheAsync invocation
/// </summary>
public class Finding7AuthBugTests
{
    // -------------------------------------------------------------------------
    // Bug A — authority unification
    // -------------------------------------------------------------------------

    /// <summary>
    /// Both GlobalDiscoveryService and InteractiveBrowserCredentialProvider must create their
    /// MSAL client with tenantId: null (→ "organizations" authority). Verified by reading the
    /// source file so we catch the call-site, not just the resulting client property.
    /// </summary>
    [Fact]
    public void BugA_InteractiveBrowserCredentialProvider_CreatesMsalClient_WithOrganizationsAuthority()
    {
        // Structural regression guard: EnsureMsalClientInitializedAsync must call
        // MsalClientBuilder.CreateClient with tenantId: null so both code paths use
        // the same "organizations" authority and share the same MSAL token cache key.
        var srcPath = GetAuthSrcPath("Credentials", "InteractiveBrowserCredentialProvider.cs");
        if (srcPath == null) return; // packaged form — skip

        var source = File.ReadAllText(srcPath);

        // Must contain the "tenantId: null" call inside EnsureMsalClientInitializedAsync
        source.Should().Contain(
            "MsalClientBuilder.CreateClient(_cloud, tenantId: null",
            because: "InteractiveBrowserCredentialProvider must use organizations authority to match GlobalDiscoveryService cache keys (Bug A fix)");
    }

    /// <summary>
    /// MsalClientBuilder.CreateClient with tenantId: null produces an authority that contains
    /// "organizations" (not a tenant GUID).
    /// </summary>
    [Fact]
    public void BugA_MsalClientBuilder_WithNullTenantId_ProducesOrganizationsAuthority()
    {
        var client = MsalClientBuilder.CreateClient(
            CloudEnvironment.Public,
            tenantId: null,
            MsalClientBuilder.RedirectUriOption.None);

        client.Authority.Should().Contain(
            "organizations",
            because: "null tenantId must resolve to the multi-tenant 'organizations' authority");
    }

    /// <summary>
    /// MsalClientBuilder.CreateClient with a specific tenantId produces a tenant-specific authority.
    /// This verifies the builder correctly uses the tenant when explicitly given (used in
    /// DeviceCodeCredentialProvider, which does NOT need the organizations unification).
    /// </summary>
    [Fact]
    public void BugA_MsalClientBuilder_WithTenantId_ProducesTenantSpecificAuthority()
    {
        const string tenantId = "12345678-1234-1234-1234-123456789012";
        var client = MsalClientBuilder.CreateClient(
            CloudEnvironment.Public,
            tenantId: tenantId,
            MsalClientBuilder.RedirectUriOption.None);

        client.Authority.Should().Contain(
            tenantId,
            because: "explicit tenantId must produce a tenant-specific authority");
    }

    /// <summary>
    /// InteractiveBrowserCredentialProvider must use WithTenantId() on AcquireToken* calls
    /// (per-request override) when a tenantId is known, so the token is issued for the right tenant
    /// even though the MSAL app uses "organizations" authority.
    /// </summary>
    [Fact]
    public void BugA_InteractiveBrowserCredentialProvider_UsesWithTenantId_OnTokenAcquisition()
    {
        var srcPath = GetAuthSrcPath("Credentials", "InteractiveBrowserCredentialProvider.cs");
        if (srcPath == null) return;

        var source = File.ReadAllText(srcPath);

        source.Should().Contain(
            ".WithTenantId(_tenantId)",
            because: "per-request tenant override via .WithTenantId() is required when using the organizations authority (Bug A fix)");
    }

    // -------------------------------------------------------------------------
    // Bug B — memory-visibility (volatile fields)
    // -------------------------------------------------------------------------

    /// <summary>
    /// _cachedResult and _cachedResultUrl must be volatile in InteractiveBrowserCredentialProvider
    /// so that a background thread running AccessTokenProviderFunctionAsync sees the value
    /// written by the main thread after interactive auth completes.
    /// </summary>
    [Fact]
    public void BugB_InteractiveBrowserCredentialProvider_CachedResultFields_AreVolatile()
    {
        var type = typeof(InteractiveBrowserCredentialProvider);
        AssertFieldIsVolatile(type, "_cachedResult",
            "without volatile the background AccessTokenProviderFunctionAsync thread may read stale null (Bug B)");
        AssertFieldIsVolatile(type, "_cachedResultUrl",
            "without volatile the background AccessTokenProviderFunctionAsync thread may read stale null (Bug B)");
    }

    /// <summary>
    /// DeviceCodeCredentialProvider has the same pattern — _cachedResult and _cachedResultUrl
    /// must also be volatile (straggler fix in same commit).
    /// </summary>
    [Fact]
    public void BugB_DeviceCodeCredentialProvider_CachedResultFields_AreVolatile()
    {
        var type = typeof(DeviceCodeCredentialProvider);
        AssertFieldIsVolatile(type, "_cachedResult",
            "DeviceCodeCredentialProvider has same AccessTokenProviderFunctionAsync background-thread pattern (Bug B straggler)");
        AssertFieldIsVolatile(type, "_cachedResultUrl",
            "DeviceCodeCredentialProvider has same AccessTokenProviderFunctionAsync background-thread pattern (Bug B straggler)");
    }

    // -------------------------------------------------------------------------
    // Bug C — stdout vs stderr
    // -------------------------------------------------------------------------

    /// <summary>
    /// AuthenticationOutput.Writer must default to Console.Error.WriteLine, not Console.WriteLine.
    /// CLI stdout is reserved for data (PPDS NEVER rule / Constitution I1).
    /// </summary>
    [Fact]
    public void BugC_AuthenticationOutput_DefaultWriter_IsStderr()
    {
        AuthenticationOutput.Reset();

        // Verify by redirecting both stdout and stderr and checking which stream receives output
        using var stdoutCapture = new StringWriter();
        using var stderrCapture = new StringWriter();

        var originalOut = Console.Out;
        var originalErr = Console.Error;

        Console.SetOut(stdoutCapture);
        Console.SetError(stderrCapture);
        try
        {
            // Invoke internal WriteLine via the current (default) writer
            typeof(AuthenticationOutput)
                .GetMethod("WriteLine", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { "bug-c-test" });
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        stdoutCapture.ToString().Should().BeEmpty(
            because: "auth status messages must NOT go to stdout (PPDS NEVER rule)");
        stderrCapture.ToString().Should().Contain(
            "bug-c-test",
            because: "auth status messages must go to stderr (Console.Error.WriteLine) by default (Bug C fix)");
    }

    /// <summary>
    /// After Reset(), the writer must still point to stderr.
    /// </summary>
    [Fact]
    public void BugC_AuthenticationOutput_Reset_RestoresToStderr()
    {
        // First set to something else
        AuthenticationOutput.Writer = _ => { };

        // Reset
        AuthenticationOutput.Reset();

        using var stdoutCapture = new StringWriter();
        using var stderrCapture = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        Console.SetOut(stdoutCapture);
        Console.SetError(stderrCapture);
        try
        {
            typeof(AuthenticationOutput)
                .GetMethod("WriteLine", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { "reset-test" });
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }

        stdoutCapture.ToString().Should().BeEmpty(
            because: "Reset() must restore to stderr, not stdout");
        stderrCapture.ToString().Should().Contain(
            "reset-test",
            because: "Reset() must restore to Console.Error.WriteLine (Bug C fix)");
    }

    // -------------------------------------------------------------------------
    // Bug D — VerifyPersistence once-per-process
    // -------------------------------------------------------------------------

    /// <summary>
    /// MsalClientBuilder must contain a static persistence-verified guard so that
    /// VerifyPersistence() is not called on every invocation of CreateAndRegisterCacheAsync.
    /// This is a structural (source-reading) test because actually invoking VerifyPersistence
    /// requires a real DPAPI/Keychain environment.
    /// </summary>
    [Fact]
    public void BugD_MsalClientBuilder_HasOncePerProcessPersistenceGuard()
    {
        var srcPath = GetAuthSrcPath("Credentials", "MsalClientBuilder.cs");
        if (srcPath == null) return;

        var source = File.ReadAllText(srcPath);

        source.Should().Contain(
            "_persistenceVerified",
            because: "MsalClientBuilder must have a once-per-process guard to avoid redundant VerifyPersistence I/O (Bug D fix)");

        // Guard must be static volatile or use a lock — check for double-checked locking pattern
        source.Should().Contain(
            "!_persistenceVerified",
            because: "the guard must be checked before and inside the lock (double-checked locking)");
    }

    /// <summary>
    /// _persistenceVerified field must be a static volatile bool to ensure visibility
    /// across threads without requiring a lock on every read.
    /// </summary>
    [Fact]
    public void BugD_MsalClientBuilder_PersistenceVerifiedField_IsStaticVolatile()
    {
        var field = typeof(MsalClientBuilder).GetField(
            "_persistenceVerified",
            BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull(
            because: "_persistenceVerified static field must exist on MsalClientBuilder (Bug D fix)");

        // FieldInfo.IsVolatile is not directly exposed; check via modifiers
        var requiredCustomModifiers = field!.GetRequiredCustomModifiers();
        var isVolatile = Array.Exists(
            requiredCustomModifiers,
            t => string.Equals(t.FullName, "System.Runtime.CompilerServices.IsVolatile", StringComparison.Ordinal));
        isVolatile.Should().BeTrue(
            because: "_persistenceVerified must be volatile to ensure cross-thread visibility without a full lock on every read");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void AssertFieldIsVolatile(Type type, string fieldName, string reason)
    {
        var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull(because: $"field '{fieldName}' must exist on {type.Name}");

        var requiredCustomModifiers = field!.GetRequiredCustomModifiers();
        var isVolatile = Array.Exists(
            requiredCustomModifiers,
            t => string.Equals(t.FullName, "System.Runtime.CompilerServices.IsVolatile", StringComparison.Ordinal));

        isVolatile.Should().BeTrue(because: reason);
    }

    private static string? GetAuthSrcPath(params string[] segments)
    {
        var thisAssembly = typeof(Finding7AuthBugTests).Assembly;
        var parts = new[] { Path.GetDirectoryName(thisAssembly.Location)!, "..", "..", "..", "..", "..", "src", "PPDS.Auth" };
        var combined = Path.Combine([.. parts, .. segments]);
        var resolved = Path.GetFullPath(combined);
        return File.Exists(resolved) ? resolved : null;
    }
}

using System.Reflection;
using FluentAssertions;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests for <c>LogIdentityHelper</c> (A5). The type is internal; reach it via reflection.
/// </summary>
public class LogIdentityHelperTests
{
    private static string InvokeHash(string? input)
    {
        var type = typeof(PPDS.Auth.Credentials.MsalClientBuilder).Assembly
            .GetType("PPDS.Auth.Credentials.LogIdentityHelper", throwOnError: true)!;
        var method = type.GetMethod("HashIdentifier", BindingFlags.Public | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object?[] { input })!;
    }

    [Fact]
    public void HashIdentifier_ProducesStableEightCharHex()
    {
        var a = InvokeHash("alice@contoso.com");
        var b = InvokeHash("alice@contoso.com");

        a.Should().Be(b);
        a.Length.Should().Be(8);
        a.Should().MatchRegex("^[0-9a-f]{8}$");
    }

    [Fact]
    public void HashIdentifier_DifferentInputs_ProduceDifferentHashes()
    {
        var a = InvokeHash("alice@contoso.com");
        var b = InvokeHash("bob@contoso.com");

        a.Should().NotBe(b);
    }

    [Fact]
    public void HashIdentifier_CaseInsensitive()
    {
        var lower = InvokeHash("alice@contoso.com");
        var mixed = InvokeHash("Alice@Contoso.COM");

        lower.Should().Be(mixed);
    }

    [Fact]
    public void HashIdentifier_NullOrEmpty_ReturnsSentinel()
    {
        InvokeHash(null).Should().Be("(none)");
        InvokeHash(string.Empty).Should().Be("(none)");
    }

    [Fact]
    public void HashIdentifier_DoesNotLeakOriginalValue()
    {
        // Ensure the hash text never contains the UPN or any portion of it.
        const string upn = "alice@contoso.com";
        var hashed = InvokeHash(upn);

        hashed.Should().NotContain("alice");
        hashed.Should().NotContain("contoso");
        hashed.Should().NotContain("@");
    }
}

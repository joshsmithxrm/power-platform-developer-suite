using System.Reflection;
using FluentAssertions;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests for <c>SensitiveValueRedactor</c> (A6). The type is internal; reach it via reflection.
/// </summary>
public class SensitiveValueRedactorTests
{
    private static string Redact(string? input)
    {
        var type = typeof(PPDS.Auth.Credentials.MsalClientBuilder).Assembly
            .GetType("PPDS.Auth.Credentials.SensitiveValueRedactor", throwOnError: true)!;
        var method = type.GetMethod("Redact", BindingFlags.Public | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object?[] { input })!;
    }

    [Fact]
    public void Redact_StripsClientSecret()
    {
        var msg = "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=abc;ClientSecret=supersecretvalue;TenantId=tid";
        var result = Redact(msg);

        result.Should().NotContain("supersecretvalue");
        result.Should().Contain("ClientSecret=***REDACTED***");
    }

    [Theory]
    [InlineData("Password=hunter2")]
    [InlineData("Secret=zzz")]
    [InlineData("AccessToken=eyJhbGciOi")]
    [InlineData("RefreshToken=r.t.v")]
    [InlineData("ApiKey=ak_test_123")]
    [InlineData("SharedAccessKey=base64==")]
    [InlineData("AccountKey=qwerty")]
    public void Redact_StripsAllKnownSensitiveKeys(string fragment)
    {
        var msg = $"prefix;{fragment};suffix";
        var result = Redact(msg);

        result.Should().Contain("***REDACTED***");
        // The secret payload itself should not remain in the redacted output.
        var value = fragment.Split('=')[1];
        result.Should().NotContain(value);
    }

    [Fact]
    public void Redact_EmptyOrNull_ReturnsEmpty()
    {
        Redact(null).Should().BeEmpty();
        Redact(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Redact_NoSensitiveKeys_LeavesMessageUnchanged()
    {
        var msg = "Failed to connect: network timeout after 30s at https://org.crm.dynamics.com/";
        Redact(msg).Should().Be(msg);
    }

    [Fact]
    public void Redact_IsCaseInsensitiveOnKeyNames()
    {
        var msg = "clientsecret=abc;TOKEN=xyz";
        var result = Redact(msg);

        result.Should().NotContain("abc");
        result.Should().NotContain("xyz");
    }
}

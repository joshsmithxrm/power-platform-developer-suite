using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Drift guard for the duplicated redactor implementations.
/// PPDS.Auth.Credentials.SensitiveValueRedactor is documented as a copy of
/// PPDS.Dataverse.Security.ConnectionStringRedactor, kept separate so PPDS.Auth
/// stays free of a Dataverse dependency. This test fails if the two
/// implementations stop producing identical output for the same input — flagging
/// the divergence at compile/CI time instead of in production logs.
/// </summary>
[Trait("Category", "Unit")]
public class SensitiveValueRedactorParityTests
{
    [Theory]
    [InlineData("AuthType=ClientSecret;ClientSecret=supersecret;ClientId=abc")]
    [InlineData("Password=hunter2;Url=https://x")]
    [InlineData("AccessToken=eyJhbGc...;RefreshToken=abc.def")]
    [InlineData("ApiKey=\"value with spaces\";Other=safe")]
    [InlineData("AccountKey=longvalue123;SharedAccessKey=othervalue")]
    [InlineData("Credential=user:pass@host;Pwd=p")]
    [InlineData("nothing-sensitive-here")]
    [InlineData("")]
    [InlineData("Secret=onlyone")]
    public void Auth_And_Dataverse_Redactors_Produce_Identical_Output(string input)
    {
        var authResult = SensitiveValueRedactor.Redact(input);
        var dataverseResult = ConnectionStringRedactor.RedactExceptionMessage(input);

        authResult.Should().Be(dataverseResult,
            "the two implementations are documented as duplicates and must stay in sync");
    }

    [Fact]
    public void Both_Redactors_Use_Same_Placeholder()
    {
        // Catches the case where the two strings drift independently.
        SensitiveValueRedactor.RedactedPlaceholder.Should().Be(ConnectionStringRedactor.RedactedPlaceholder);
    }

    [Theory]
    [InlineData(null)]
    public void Both_Redactors_Handle_Null_Identically(string? input)
    {
        var authResult = SensitiveValueRedactor.Redact(input);
        var dataverseResult = ConnectionStringRedactor.RedactExceptionMessage(input);

        authResult.Should().Be(dataverseResult);
    }
}

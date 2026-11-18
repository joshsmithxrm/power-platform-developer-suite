using Xunit;

namespace PPDS.LiveTests.Query;

/// <summary>The DML live suite runs only against an organisation named explicitly for the run.</summary>
[Trait("Category", "Unit")]
public class DmlLiveConfirmUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.crm.dynamics.com")]
    [InlineData("https://example.crm.dynamics.com/main.aspx")]
    [InlineData("example.crm.dynamics.com")]
    public void AMissingOrMalformedConfirmUrl_StopsTheSuite(string? value)
        => Assert.Throws<InvalidOperationException>(() => DmlLiveDifferentialTests.ConfirmedUrl(value));

    [Theory]
    [InlineData("https://example.crm.dynamics.com", "https://example.crm.dynamics.com")]
    [InlineData(" https://example.crm.dynamics.com/ ", "https://example.crm.dynamics.com")]
    public void AnOrganisationUrl_IsTheOnlyOneTheSuiteAccepts(string value, string expected)
        => Assert.Equal(expected, DmlLiveDifferentialTests.ConfirmedUrl(value));
}

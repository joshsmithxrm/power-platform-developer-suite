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

    // C-S6: an unset confirm URL skips the run with the reason (it never fails it, and never runs it).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnUnsetConfirmUrl_SkipsTheRun_WithTheReason(string? value)
        => Assert.Contains("PPDS_DML_LIVE_CONFIRM_URL is not set", DmlLiveSkip.ForDifferentialRun(value, null));

    [Fact]
    public void ASetConfirmUrl_RunsTheDifferentialRun()
        => Assert.Null(DmlLiveSkip.ForDifferentialRun("https://example.crm.dynamics.com", null));

    // C-M1: cleanup-only is its own operation; it never makes the differential run report a pass.
    [Fact]
    public void CleanupOnly_SkipsTheDifferentialRun_AndRunsOnlyTheCleanupOperation()
    {
        Assert.Contains("cleanup operation only", DmlLiveSkip.ForDifferentialRun("https://example.crm.dynamics.com", "1"));
        Assert.Null(DmlLiveSkip.ForCleanup("https://example.crm.dynamics.com", "1"));
        Assert.NotNull(DmlLiveSkip.ForCleanup("https://example.crm.dynamics.com", null));
        Assert.NotNull(DmlLiveSkip.ForCleanup(null, "1"));
    }
}

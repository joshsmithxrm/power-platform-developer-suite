using FluentAssertions;
using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Services.UpdateCheck;

public class NuGetVersionTests
{
    #region Parsing - Valid Versions

    [Fact]
    public void Parse_StableVersion_ParsesCorrectly()
    {
        var v = NuGetVersion.Parse("1.2.3");

        v.Major.Should().Be(1);
        v.Minor.Should().Be(2);
        v.Patch.Should().Be(3);
        v.PreReleaseLabel.Should().Be(string.Empty);
        v.IsPreRelease.Should().BeFalse();
    }

    [Fact]
    public void Parse_PreReleaseVersion_ParsesCorrectly()
    {
        var v = NuGetVersion.Parse("1.2.3-beta.1");

        v.Major.Should().Be(1);
        v.Minor.Should().Be(2);
        v.Patch.Should().Be(3);
        v.PreReleaseLabel.Should().Be("beta.1");
        v.IsPreRelease.Should().BeTrue();
    }

    [Fact]
    public void Parse_VersionWithBuildMetadata_StripsMetadata()
    {
        var v = NuGetVersion.Parse("1.2.3+abc1234");

        v.Major.Should().Be(1);
        v.Minor.Should().Be(2);
        v.Patch.Should().Be(3);
        v.PreReleaseLabel.Should().Be(string.Empty);
        v.IsPreRelease.Should().BeFalse();
    }

    [Fact]
    public void Parse_InformationalVersionFormat_ParsesCorrectly()
    {
        // e.g., from AssemblyInformationalVersionAttribute: "1.2.3-beta.1+abc1234"
        var v = NuGetVersion.Parse("1.2.3-beta.1+abc1234");

        v.Major.Should().Be(1);
        v.Minor.Should().Be(2);
        v.Patch.Should().Be(3);
        v.PreReleaseLabel.Should().Be("beta.1");
        v.IsPreRelease.Should().BeTrue();
    }

    [Fact]
    public void Parse_ZeroVersion_ParsesCorrectly()
    {
        var v = NuGetVersion.Parse("0.0.0");

        v.Major.Should().Be(0);
        v.Minor.Should().Be(0);
        v.Patch.Should().Be(0);
    }

    [Fact]
    public void Parse_LargeNumbers_ParsesCorrectly()
    {
        var v = NuGetVersion.Parse("100.200.300");

        v.Major.Should().Be(100);
        v.Minor.Should().Be(200);
        v.Patch.Should().Be(300);
    }

    #endregion

    #region Parsing - Pre-Release Detection

    [Theory]
    [InlineData("1.0.0-alpha", true)]
    [InlineData("1.0.0-beta.1", true)]
    [InlineData("1.0.0-rc.1", true)]
    [InlineData("1.0.0-alpha.0", true)]
    [InlineData("1.0.0", false)]
    [InlineData("2.5.0", false)]
    public void IsPreRelease_ReturnsCorrectValue(string version, bool expected)
    {
        var v = NuGetVersion.Parse(version);
        v.IsPreRelease.Should().Be(expected);
    }

    #endregion

    #region IsOddMinor

    [Theory]
    [InlineData("1.0.0", false)]
    [InlineData("1.1.0", true)]
    [InlineData("1.2.0", false)]
    [InlineData("1.3.0", true)]
    [InlineData("2.0.0", false)]
    [InlineData("2.1.0", true)]
    [InlineData("0.1.0", true)]
    [InlineData("0.0.0", false)]
    public void IsOddMinor_ReturnsCorrectValue(string version, bool expected)
    {
        var v = NuGetVersion.Parse(version);
        v.IsOddMinor.Should().Be(expected);
    }

    #endregion

    #region TryParse

    [Fact]
    public void TryParse_ValidVersion_ReturnsTrueAndResult()
    {
        var success = NuGetVersion.TryParse("1.2.3", out var result);

        success.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Major.Should().Be(1);
        result.Minor.Should().Be(2);
        result.Patch.Should().Be(3);
    }

    [Fact]
    public void TryParse_NullInput_ReturnsFalseAndNull()
    {
        var success = NuGetVersion.TryParse(null, out var result);

        success.Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalseAndNull()
    {
        var success = NuGetVersion.TryParse(string.Empty, out var result);

        success.Should().BeFalse();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData(".1.2")]
    public void TryParse_InvalidInput_ReturnsFalseAndNull(string input)
    {
        var success = NuGetVersion.TryParse(input, out var result);

        success.Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void TryParse_ValidVersionWithPreRelease_ReturnsTrueAndResult()
    {
        var success = NuGetVersion.TryParse("2.0.0-alpha.1+build999", out var result);

        success.Should().BeTrue();
        result.Should().NotBeNull();
        result!.PreReleaseLabel.Should().Be("alpha.1");
    }

    #endregion

    #region ToString Round-Trip

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("2.3.4-rc.2")]
    public void ToString_StableAndPreRelease_RoundTrips(string version)
    {
        // Build metadata is stripped; stable and pre-release should round-trip
        var v = NuGetVersion.Parse(version);
        v.ToString().Should().Be(version);
    }

    [Fact]
    public void ToString_WithBuildMetadata_StripsMetadata()
    {
        var v = NuGetVersion.Parse("1.2.3-beta.1+abc1234");
        v.ToString().Should().Be("1.2.3-beta.1");
    }

    #endregion

    #region Comparison - Basic Ordering

    [Fact]
    public void CompareTo_HigherMajor_Wins()
    {
        var v1 = NuGetVersion.Parse("2.0.0");
        var v2 = NuGetVersion.Parse("1.0.0");

        v1.CompareTo(v2).Should().BePositive();
        v2.CompareTo(v1).Should().BeNegative();
    }

    [Fact]
    public void CompareTo_HigherMinor_Wins()
    {
        var v1 = NuGetVersion.Parse("1.2.0");
        var v2 = NuGetVersion.Parse("1.1.0");

        v1.CompareTo(v2).Should().BePositive();
        v2.CompareTo(v1).Should().BeNegative();
    }

    [Fact]
    public void CompareTo_HigherPatch_Wins()
    {
        var v1 = NuGetVersion.Parse("1.0.1");
        var v2 = NuGetVersion.Parse("1.0.0");

        v1.CompareTo(v2).Should().BePositive();
        v2.CompareTo(v1).Should().BeNegative();
    }

    [Fact]
    public void CompareTo_EqualVersions_ReturnsZero()
    {
        var v1 = NuGetVersion.Parse("1.2.3");
        var v2 = NuGetVersion.Parse("1.2.3");

        v1.CompareTo(v2).Should().Be(0);
    }

    #endregion

    #region Comparison - Stable Beats Pre-Release

    [Fact]
    public void CompareTo_StableBeatsPreReleaseAtSameBase()
    {
        var stable = NuGetVersion.Parse("1.0.0");
        var preRelease = NuGetVersion.Parse("1.0.0-beta.1");

        stable.CompareTo(preRelease).Should().BePositive();
        preRelease.CompareTo(stable).Should().BeNegative();
    }

    [Fact]
    public void CompareTo_TwoPreReleasesSameBase_LabelComparison()
    {
        var alpha = NuGetVersion.Parse("1.0.0-alpha.1");
        var beta = NuGetVersion.Parse("1.0.0-beta.1");

        // "beta" > "alpha" alphabetically
        beta.CompareTo(alpha).Should().BePositive();
        alpha.CompareTo(beta).Should().BeNegative();
    }

    #endregion

    #region Comparison - Multi-Digit Numeric Pre-Release Segments

    [Fact]
    public void CompareTo_NumericPreReleaseSegments_ComparedAsIntegers()
    {
        var beta10 = NuGetVersion.Parse("1.0.0-beta.10");
        var beta2 = NuGetVersion.Parse("1.0.0-beta.2");

        // beta.10 > beta.2 when compared as integers (not strings where "10" < "2")
        beta10.CompareTo(beta2).Should().BePositive();
        beta2.CompareTo(beta10).Should().BeNegative();
    }

    [Fact]
    public void CompareTo_NumericPreReleaseSegments_SingleDigitEqualMultiDigit()
    {
        var v10 = NuGetVersion.Parse("1.0.0-alpha.10");
        var v10b = NuGetVersion.Parse("1.0.0-alpha.10");

        v10.CompareTo(v10b).Should().Be(0);
    }

    [Fact]
    public void CompareTo_NumericVsStringSegments_NumericSortsBefore()
    {
        // Per SemVer: numeric identifiers always have lower precedence than alphanumeric identifiers
        var numeric = NuGetVersion.Parse("1.0.0-1");
        var alphanumeric = NuGetVersion.Parse("1.0.0-alpha");

        numeric.CompareTo(alphanumeric).Should().BeNegative();
        alphanumeric.CompareTo(numeric).Should().BePositive();
    }

    [Fact]
    public void CompareTo_AlphaZeroVsAlphaTen_AlphaTenWins()
    {
        var alpha0 = NuGetVersion.Parse("1.0.0-alpha.0");
        var alpha10 = NuGetVersion.Parse("1.0.0-alpha.10");

        alpha10.CompareTo(alpha0).Should().BePositive();
    }

    #endregion

    #region Comparison - Cross-Minor

    [Fact]
    public void CompareTo_CrossMinor_HigherMinorWins()
    {
        var v120 = NuGetVersion.Parse("1.2.0");
        var v110 = NuGetVersion.Parse("1.1.9");

        v120.CompareTo(v110).Should().BePositive();
    }

    [Fact]
    public void CompareTo_CrossMinorPreRelease_StableOlderMinorLosesToNewerMinorPreRelease()
    {
        // 1.2.0-beta.1 > 1.1.0 (major.minor.patch take precedence over pre-release)
        var v120beta = NuGetVersion.Parse("1.2.0-beta.1");
        var v110 = NuGetVersion.Parse("1.1.0");

        v120beta.CompareTo(v110).Should().BePositive();
    }

    #endregion

    #region Operators

    [Fact]
    public void GreaterThan_Operator_WorksCorrectly()
    {
        var v2 = NuGetVersion.Parse("2.0.0");
        var v1 = NuGetVersion.Parse("1.0.0");

        (v2 > v1).Should().BeTrue();
        (v1 > v2).Should().BeFalse();
    }

    [Fact]
    public void LessThan_Operator_WorksCorrectly()
    {
        var v2 = NuGetVersion.Parse("2.0.0");
        var v1 = NuGetVersion.Parse("1.0.0");

        (v1 < v2).Should().BeTrue();
        (v2 < v1).Should().BeFalse();
    }

    [Fact]
    public void GreaterThanOrEqual_Operator_WorksCorrectly()
    {
        var v1a = NuGetVersion.Parse("1.0.0");
        var v1b = NuGetVersion.Parse("1.0.0");
        var v2 = NuGetVersion.Parse("2.0.0");

        (v2 >= v1a).Should().BeTrue();
        (v1a >= v1b).Should().BeTrue();
        (v1a >= v2).Should().BeFalse();
    }

    [Fact]
    public void LessThanOrEqual_Operator_WorksCorrectly()
    {
        var v1a = NuGetVersion.Parse("1.0.0");
        var v1b = NuGetVersion.Parse("1.0.0");
        var v2 = NuGetVersion.Parse("2.0.0");

        (v1a <= v2).Should().BeTrue();
        (v1a <= v1b).Should().BeTrue();
        (v2 <= v1a).Should().BeFalse();
    }

    #endregion

    #region Equality

    [Fact]
    public void Equals_SameVersion_ReturnsTrue()
    {
        var v1 = NuGetVersion.Parse("1.2.3-beta.1");
        var v2 = NuGetVersion.Parse("1.2.3-beta.1");

        v1.Equals(v2).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentVersion_ReturnsFalse()
    {
        var v1 = NuGetVersion.Parse("1.2.3");
        var v2 = NuGetVersion.Parse("1.2.4");

        v1.Equals(v2).Should().BeFalse();
    }

    [Fact]
    public void Equals_BuildMetadataStripped_SameVersionConsideredEqual()
    {
        var v1 = NuGetVersion.Parse("1.2.3-beta.1+build123");
        var v2 = NuGetVersion.Parse("1.2.3-beta.1+build456");

        // Build metadata is stripped; should be equal
        v1.Equals(v2).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_EqualVersions_SameHashCode()
    {
        var v1 = NuGetVersion.Parse("1.2.3");
        var v2 = NuGetVersion.Parse("1.2.3");

        v1.GetHashCode().Should().Be(v2.GetHashCode());
    }

    #endregion

    #region Parse - Error Cases

    [Fact]
    public void Parse_InvalidVersion_ThrowsFormatException()
    {
        var act = () => NuGetVersion.Parse("not-valid");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_NullVersion_ThrowsArgumentNullException()
    {
        var act = () => NuGetVersion.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}

using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using PPDS.Dataverse.Metadata;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata;

/// <summary>
/// Tests for the filter regex behavior in DataverseMetadataQueryService.
/// </summary>
public class FilterRegexTests
{
    private static Regex? CreateFilterRegex(string? filter)
    {
        // Use reflection to call the private static method
        var method = typeof(DataverseMetadataQueryService)
            .GetMethod("CreateFilterRegex", BindingFlags.NonPublic | BindingFlags.Static);

        return (Regex?)method?.Invoke(null, [filter]);
    }

    [Fact]
    public void CreateFilterRegex_NullFilter_ReturnsNull()
    {
        var regex = CreateFilterRegex(null);
        regex.Should().BeNull();
    }

    [Fact]
    public void CreateFilterRegex_EmptyFilter_ReturnsNull()
    {
        var regex = CreateFilterRegex("");
        regex.Should().BeNull();
    }

    [Fact]
    public void CreateFilterRegex_WhitespaceFilter_ReturnsNull()
    {
        var regex = CreateFilterRegex("   ");
        regex.Should().BeNull();
    }

    [Theory]
    [InlineData("zipcode", "ppds_zipcode", true)]  // Contains match
    [InlineData("zipcode", "zipcode", true)]       // Exact match still works
    [InlineData("zipcode", "zipcode_lookup", true)] // Contains at start
    [InlineData("zipcode", "account", false)]      // No match
    public void CreateFilterRegex_WithoutWildcard_MatchesContaining(
        string filter, string testValue, bool shouldMatch)
    {
        var regex = CreateFilterRegex(filter);

        regex.Should().NotBeNull();
        regex!.IsMatch(testValue).Should().Be(shouldMatch,
            $"'{filter}' (no wildcards = contains) should {(shouldMatch ? "" : "not ")}match '{testValue}'");
    }

    [Theory]
    [InlineData("zipcode*", "zipcode", true)]        // Starts with - exact
    [InlineData("zipcode*", "zipcode_lookup", true)] // Starts with
    [InlineData("zipcode*", "ppds_zipcode", false)]  // Does not start with
    public void CreateFilterRegex_StartsWithWildcard_MatchesStartsWith(
        string filter, string testValue, bool shouldMatch)
    {
        var regex = CreateFilterRegex(filter);

        regex.Should().NotBeNull();
        regex!.IsMatch(testValue).Should().Be(shouldMatch,
            $"'{filter}' (starts with) should {(shouldMatch ? "" : "not ")}match '{testValue}'");
    }

    [Theory]
    [InlineData("*zipcode", "zipcode", true)]        // Ends with - exact
    [InlineData("*zipcode", "ppds_zipcode", true)]   // Ends with
    [InlineData("*zipcode", "zipcode_lookup", false)] // Does not end with
    public void CreateFilterRegex_EndsWithWildcard_MatchesEndsWith(
        string filter, string testValue, bool shouldMatch)
    {
        var regex = CreateFilterRegex(filter);

        regex.Should().NotBeNull();
        regex!.IsMatch(testValue).Should().Be(shouldMatch,
            $"'{filter}' (ends with) should {(shouldMatch ? "" : "not ")}match '{testValue}'");
    }

    [Theory]
    [InlineData("*zipcode*", "zipcode", true)]
    [InlineData("*zipcode*", "ppds_zipcode", true)]
    [InlineData("*zipcode*", "zipcode_lookup", true)]
    [InlineData("*zipcode*", "ppds_zipcode_lookup", true)]
    [InlineData("*zipcode*", "account", false)]
    public void CreateFilterRegex_ContainsWildcard_MatchesContaining(
        string filter, string testValue, bool shouldMatch)
    {
        var regex = CreateFilterRegex(filter);

        regex.Should().NotBeNull();
        regex!.IsMatch(testValue).Should().Be(shouldMatch,
            $"'{filter}' (explicit contains) should {(shouldMatch ? "" : "not ")}match '{testValue}'");
    }

    [Theory]
    [InlineData("Account", "account", true)]  // Case insensitive
    [InlineData("ACCOUNT", "account", true)]
    [InlineData("account", "ACCOUNT", true)]
    public void CreateFilterRegex_IsCaseInsensitive(
        string filter, string testValue, bool shouldMatch)
    {
        var regex = CreateFilterRegex(filter);

        regex.Should().NotBeNull();
        regex!.IsMatch(testValue).Should().Be(shouldMatch);
    }

    [Fact]
    public void CreateFilterRegex_SpecialRegexCharacters_AreEscaped()
    {
        // Filter with special regex characters should be escaped
        var regex = CreateFilterRegex("test.name");

        regex.Should().NotBeNull();
        regex!.IsMatch("testXname").Should().BeFalse("'.' should be literal, not regex wildcard");
        regex!.IsMatch("test.name").Should().BeTrue("literal '.' should match");
    }

    [Theory]
    [InlineData("new_*", "new_custom_entity", true)]
    [InlineData("new_*", "old_custom_entity", false)]
    [InlineData("ppds_*", "ppds_zipcode", true)]
    public void CreateFilterRegex_PrefixPattern_MatchesCustomEntities(
        string filter, string testValue, bool shouldMatch)
    {
        var regex = CreateFilterRegex(filter);

        regex.Should().NotBeNull();
        regex!.IsMatch(testValue).Should().Be(shouldMatch);
    }
}

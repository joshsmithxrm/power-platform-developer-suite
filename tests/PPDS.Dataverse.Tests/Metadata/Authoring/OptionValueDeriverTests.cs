using FluentAssertions;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;

namespace PPDS.Dataverse.Tests.Metadata.Authoring;

/// <summary>
/// Tests for OptionValueDeriver.Derive — covers AC-57.
/// </summary>
[Trait("Category", "Unit")]
public class OptionValueDeriverTests
{
    [Fact]
    public void Derive_ExplicitValue_WinsOverPrefix()
    {
        var result = OptionValueDeriver.Derive(
            explicitValue: 99,
            publisherOptionPrefix: 10,
            existingValues: []);

        result.Should().Be(99);
    }

    [Fact]
    public void Derive_ExplicitValue_CollisionThrows()
    {
        var act = () => OptionValueDeriver.Derive(
            explicitValue: 99,
            publisherOptionPrefix: null,
            existingValues: [99]);

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.DuplicateOptionValue);
    }

    [Fact]
    public void Derive_PrefixBase_IsPrefix_Times_10000()
    {
        var result = OptionValueDeriver.Derive(
            explicitValue: null,
            publisherOptionPrefix: 10,
            existingValues: []);

        result.Should().Be(100_000);
    }

    [Fact]
    public void Derive_PrefixGapFill_AdvancesPastExisting()
    {
        var result = OptionValueDeriver.Derive(
            explicitValue: null,
            publisherOptionPrefix: 10,
            existingValues: [100_000, 100_001, 100_002]);

        result.Should().Be(100_003);
    }

    [Fact]
    public void Derive_PrefixWithGap_FindsFirstFree()
    {
        var result = OptionValueDeriver.Derive(
            explicitValue: null,
            publisherOptionPrefix: 10,
            existingValues: [100_000, 100_002]);

        result.Should().Be(100_001);
    }

    [Fact]
    public void Derive_NeitherValueNorPrefix_Throws()
    {
        var act = () => OptionValueDeriver.Derive(
            explicitValue: null,
            publisherOptionPrefix: null,
            existingValues: []);

        act.Should().Throw<MetadataValidationException>()
            .Which.ErrorCode.Should().Be(MetadataErrorCodes.MissingRequiredField);
    }

    [Fact]
    public void Derive_ExplicitValue_EmptyExisting_Returns_Value()
    {
        var result = OptionValueDeriver.Derive(
            explicitValue: 42,
            publisherOptionPrefix: null,
            existingValues: []);

        result.Should().Be(42);
    }

    [Fact]
    public void Derive_PrefixWithManyExisting_AdvancesCorrectly()
    {
        var existing = Enumerable.Range(100_000, 50).ToList();
        var result = OptionValueDeriver.Derive(
            explicitValue: null,
            publisherOptionPrefix: 10,
            existingValues: existing);

        result.Should().Be(100_050);
    }
}

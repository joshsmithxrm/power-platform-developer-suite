using FluentAssertions;
using PPDS.Cli.Services.Data;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// The id spelling rule of the data commands (#1915): exactly 8-4-4-4-12 hexadecimal digits, either case, optionally in
/// one pair of braces. Pinned as a table, because .NET's GUID parser reads some damaged ids as different ids.
/// </summary>
[Trait("Category", "Unit")]
public class RecordIdTextTests
{
    private const string Id = "cccccccc-0000-0000-0000-000000000001";

    public static TheoryData<string, string> Accepted => new()
    {
        { "cccccccc-0000-0000-0000-000000000001", Id },
        { "CCCCCCCC-0000-0000-0000-000000000001", Id },
        { "CcCcCcCc-0000-0000-0000-000000000001", Id },
        { "{cccccccc-0000-0000-0000-000000000001}", Id },
        { "{CCCCCCCC-0000-0000-0000-000000000001}", Id },
        { "0123abcd-ef45-6789-abcd-ef0123456789", "0123abcd-ef45-6789-abcd-ef0123456789" },
    };

    public static TheoryData<string> Refused => new()
    {
        // The damaged ids .NET's parser reads as other ids, even in its exact "D" mode.
        "+ccccccc-0000-0000-0000-000000000001",
        "0xcccccc-0000-0000-0000-000000000001",
        "0Xcccccc-0000-0000-0000-000000000001",
        "cccccccc-0x12-0000-0000-000000000001",
        "cccccccc-+000-0000-0000-000000000001",
        "cccccccc-0000-0000-0000-+00000000001",
        // Other spellings the parser accepts.
        "cccccccc000000000000000000000001",
        "(cccccccc-0000-0000-0000-000000000001)",
        "{0xcccccccc,0x0000,0x0000,{0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x01}}",
        // White space anywhere.
        " cccccccc-0000-0000-0000-000000000001",
        "cccccccc-0000-0000-0000-000000000001 ",
        " cccccccc-0000-0000-0000-000000000001",
        "cccccccc-0000-0000-0000-000000000001\n",
        "{ cccccccc-0000-0000-0000-000000000001}",
        // Wrong shape.
        "{cccccccc-0000-0000-0000-000000000001",
        "cccccccc-0000-0000-0000-000000000001}",
        "{{cccccccc-0000-0000-0000-000000000001}}",
        "cccccccc-0000-0000-0000-00000000001",
        "cccccccc-0000-0000-0000-0000000000011",
        "ccccccc-c0000-0000-0000-000000000001",
        "gccccccc-0000-0000-0000-000000000001",
        "cccccccc_0000_0000_0000_000000000001",
        "ｃccccccc-0000-0000-0000-000000000001",
        // The empty id is not a record.
        "00000000-0000-0000-0000-000000000000",
        "{00000000-0000-0000-0000-000000000000}",
        "",
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Accepted_ReadAsTheSameId(string text, string expected)
    {
        RecordIdText.TryParse(text, out var id).Should().BeTrue();
        id.Should().Be(Guid.Parse(expected));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Refused_EvenWhereTheGuidParserAcceptsIt(string text)
    {
        RecordIdText.TryParse(text, out var id).Should().BeFalse();
        id.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Null_IsRefused()
    {
        RecordIdText.TryParse(null, out _).Should().BeFalse();
    }
}

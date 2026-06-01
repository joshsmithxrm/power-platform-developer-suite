using FluentAssertions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

public class ClassIdResolverTests
{
    #region AC-17: Correct classid per type

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_String_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("String");

        result.Should().Be("{4273EDBD-AC1D-40d3-9FB2-095C621B552D}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Money_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Money");

        result.Should().Be("{533B9108-5A8B-42cb-BD37-52D1B8E7C741}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Picklist_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Picklist");

        result.Should().Be("{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Lookup_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Lookup");

        result.Should().Be("{270BD3DB-D9AF-4782-9025-509E298DEC0A}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_DateTime_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("DateTime");

        result.Should().Be("{5B773807-9FB2-42db-97C3-7A91EFF8ADFF}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Integer_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Integer");

        result.Should().Be("{C6D124CA-7EDA-4a60-AEA9-7FB8D318B68F}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Decimal_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Decimal");

        result.Should().Be("{C3EFE0C3-0EC6-42be-8349-CBD9079C5A6F}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Boolean_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Boolean");

        result.Should().Be("{67FAC785-CD58-4f9f-ABB3-4B7DDC6ED5ED}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_Memo_ReturnsCorrectClassId()
    {
        var result = ClassIdResolver.ResolveForField("Memo");

        result.Should().Be("{E0DECE4B-6FC8-4a8f-A065-082708572369}");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("String")]
    [InlineData("Money")]
    [InlineData("Picklist")]
    [InlineData("Lookup")]
    [InlineData("DateTime")]
    [InlineData("Integer")]
    [InlineData("Decimal")]
    [InlineData("Boolean")]
    [InlineData("Memo")]
    public void Resolve_AllSupportedTypes_ReturnsCorrectClassId(string attributeType)
    {
        var result = ClassIdResolver.ResolveForField(attributeType);

        result.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region AC-18: Unsupported type throws

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_UnsupportedType_ThrowsUnsupportedColumnType()
    {
        var act = () => ClassIdResolver.ResolveForField("BigInt");

        act.Should().Throw<PpdsException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.UnsupportedColumnType);
    }

    #endregion

    #region Additional coverage

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_CaseInsensitive_Works()
    {
        var lower = ClassIdResolver.ResolveForField("string");
        var titleCase = ClassIdResolver.ResolveForField("String");

        lower.Should().Be(titleCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SubgridClassId_IsCorrectValue()
    {
        ClassIdResolver.SubgridClassId.Should().Be("{E7A81278-8635-4d9e-8D4D-59480B391C5B}");
    }

    #endregion
}

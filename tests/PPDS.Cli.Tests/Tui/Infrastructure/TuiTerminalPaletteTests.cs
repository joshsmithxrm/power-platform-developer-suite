using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

/// <summary>
/// Unit tests for <see cref="TuiTerminalPalette"/>.
/// Validates OSC 4 escape sequence format and palette definitions.
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class TuiTerminalPaletteTests
{
    [Fact]
    public void BuildApplySequence_ContainsAll16ColorIndices()
    {
        var sequence = TuiTerminalPalette.BuildApplySequence();

        for (int i = 0; i <= 15; i++)
        {
            Assert.Contains($"\x1b]4;{i};rgb:", sequence);
        }
    }

    [Fact]
    public void BuildApplySequence_UsesCorrectOsc4Format()
    {
        var sequence = TuiTerminalPalette.BuildApplySequence();

        // Index 0 (Black) = pure black
        Assert.Contains("\x1b]4;0;rgb:00/00/00\x07", sequence);

        // Index 15 (White) = pure white
        Assert.Contains("\x1b]4;15;rgb:ff/ff/ff\x07", sequence);
    }

    [Fact]
    public void BuildRestoreSequence_EmitsOsc104()
    {
        var sequence = TuiTerminalPalette.BuildRestoreSequence();

        Assert.Equal("\x1b]104\x07", sequence);
    }

    [Fact]
    public void GetPaletteDefinitions_Returns16Entries()
    {
        var palette = TuiTerminalPalette.GetPaletteDefinitions();

        Assert.Equal(16, palette.Count);
    }

    [Fact]
    public void GetPaletteDefinitions_IndicesAre0Through15()
    {
        var palette = TuiTerminalPalette.GetPaletteDefinitions();
        var indices = palette.Select(p => p.Index).OrderBy(i => i).ToList();

        Assert.Equal(Enumerable.Range(0, 16).ToList(), indices);
    }

    [Fact]
    public void GetPaletteDefinitions_AllValuesAreTwoCharHex()
    {
        var palette = TuiTerminalPalette.GetPaletteDefinitions();

        foreach (var (index, r, g, b) in palette)
        {
            Assert.Matches("^[0-9a-f]{2}$", r);
            Assert.Matches("^[0-9a-f]{2}$", g);
            Assert.Matches("^[0-9a-f]{2}$", b);
        }
    }

    [Fact]
    public void GetPaletteDefinitions_BlackIsPureBlack()
    {
        var palette = TuiTerminalPalette.GetPaletteDefinitions();
        var black = palette.First(p => p.Index == 0);

        Assert.Equal("00", black.R);
        Assert.Equal("00", black.G);
        Assert.Equal("00", black.B);
    }

    [Fact]
    public void GetPaletteDefinitions_WhiteIsPureWhite()
    {
        var palette = TuiTerminalPalette.GetPaletteDefinitions();
        var white = palette.First(p => p.Index == 15);

        Assert.Equal("ff", white.R);
        Assert.Equal("ff", white.G);
        Assert.Equal("ff", white.B);
    }
}

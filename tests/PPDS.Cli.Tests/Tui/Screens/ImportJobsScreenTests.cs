using PPDS.Cli.Tui.Screens;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

/// <summary>
/// Tests for <see cref="ImportJobsScreen.FormatImportJobData(string?)"/> — the
/// pretty-printer that fixes #763 (single-line XML in the import job detail
/// dialog).
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class ImportJobsScreenTests
{
    [Fact]
    public void FormatImportJobData_NullInput_ReturnsPlaceholder()
    {
        var result = ImportJobsScreen.FormatImportJobData(null);

        Assert.Equal("(No import log data available)", result);
    }

    [Fact]
    public void FormatImportJobData_EmptyInput_ReturnsPlaceholder()
    {
        var result = ImportJobsScreen.FormatImportJobData(string.Empty);

        Assert.Equal("(No import log data available)", result);
    }

    [Fact]
    public void FormatImportJobData_WhitespaceInput_ReturnsPlaceholder()
    {
        var result = ImportJobsScreen.FormatImportJobData("   \t  ");

        Assert.Equal("(No import log data available)", result);
    }

    [Fact]
    public void FormatImportJobData_SingleLineXml_IsPrettyPrinted()
    {
        const string singleLine = "<importexportxml><solutionManifests><solutionManifest><UniqueName>Test</UniqueName></solutionManifest></solutionManifests></importexportxml>";

        var result = ImportJobsScreen.FormatImportJobData(singleLine);

        // Must be multi-line after formatting.
        var lineCount = result.Split('\n').Length;
        Assert.True(lineCount > 1, $"Expected multi-line output, got {lineCount} line(s): {result}");
        Assert.Contains("<importexportxml>", result);
        Assert.Contains("<UniqueName>Test</UniqueName>", result);
    }

    [Fact]
    public void FormatImportJobData_MalformedXml_FallsBackToRaw()
    {
        const string malformed = "<not-valid><unclosed>";

        var result = ImportJobsScreen.FormatImportJobData(malformed);

        // Fallback: raw string is returned so the user can still inspect it.
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void FormatImportJobData_PreservesCData()
    {
        const string withCData = "<root><node><![CDATA[<script>alert('x')</script>]]></node></root>";

        var result = ImportJobsScreen.FormatImportJobData(withCData);

        Assert.Contains("<![CDATA[", result);
        Assert.Contains("<script>alert('x')</script>", result);
    }

    [Fact]
    public void FormatImportJobData_PreservesComments()
    {
        const string withComments = "<root><!-- keep me --><child/></root>";

        var result = ImportJobsScreen.FormatImportJobData(withComments);

        Assert.Contains("<!-- keep me -->", result);
    }
}

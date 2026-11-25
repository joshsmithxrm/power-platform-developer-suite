using FluentAssertions;
using PPDS.Cli.Services.Data;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>The contract of <see cref="StrictCsvReader"/>: rows as lists of fields with the line each starts on.</summary>
[Trait("Category", "Unit")]
public class StrictCsvReaderTests
{
    [Fact]
    public void RowsCarryTheirFieldsAndStartingLines()
    {
        var table = StrictCsvReader.Read("a,b\r\n1,\"x, y\"\n\n   \n2,\"multi\nline \"\"quoted\"\"\"\n3,z");

        table.IsExact.Should().BeTrue();
        table.Header!.Fields.Should().Equal("a", "b");
        table.Rows.Select(r => r.LineNumber).Should().Equal(2, 5, 7);
        table.Rows[0].Fields.Should().Equal("1", "x, y");
        table.Rows[1].Fields.Should().Equal("2", "multi\nline \"quoted\"");
    }

    [Fact]
    public void FieldCountMismatchesAreListed_WithoutContent()
    {
        var table = StrictCsvReader.Read("a,b,c\n1,2\n1,2,3\n1,2,3,4\n");

        table.IsExact.Should().BeFalse();
        table.FieldCountMismatches.Should().Equal(new CsvFieldCountMismatch(2, 2), new CsvFieldCountMismatch(4, 4));
        table.DescribeProblems().Should().ContainSingle()
            .Which.Should().Be("2 records have a number of fields other than the header's 3 (line 2: 2, line 4: 4)");
    }

    [Fact]
    public void ManyMismatches_AreCapped()
    {
        var rows = string.Concat(Enumerable.Range(0, 15).Select(_ => "1\n"));
        var table = StrictCsvReader.Read("a,b\n" + rows);

        table.DescribeProblems(maxLines: 10).Single().Should().EndWith("line 11: 1, and 5 more)");
    }

    [Fact]
    public void UnclosedQuote_IsReportedWithTheLineItOpens()
    {
        var table = StrictCsvReader.Read("a,b\n1,\"open\n2,3\n");

        table.IsExact.Should().BeFalse();
        table.UnclosedQuoteLine.Should().Be(2);
    }

    [Fact]
    public void EmptyContent_HasNoHeader()
    {
        StrictCsvReader.Read("").Header.Should().BeNull();
    }
}

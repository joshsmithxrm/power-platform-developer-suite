using FluentAssertions;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Models;

public class FileColumnDataTests
{
    [Fact]
    public void Constructor_InitializesWithDefaults()
    {
        var data = new FileColumnData();

        data.RecordId.Should().Be(Guid.Empty);
        data.FieldName.Should().BeEmpty();
        data.FileName.Should().BeEmpty();
        data.MimeType.Should().BeEmpty();
        data.Data.Should().BeEmpty();
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        var recordId = Guid.NewGuid();
        var fileBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04 };

        var data = new FileColumnData
        {
            RecordId = recordId,
            FieldName = "cr_document",
            FileName = "report.pdf",
            MimeType = "application/pdf",
            Data = fileBytes
        };

        data.RecordId.Should().Be(recordId);
        data.FieldName.Should().Be("cr_document");
        data.FileName.Should().Be("report.pdf");
        data.MimeType.Should().Be("application/pdf");
        data.Data.Should().Equal(fileBytes);
    }
}

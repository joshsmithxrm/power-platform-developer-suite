using System.IO.Compression;
using System.Text;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Extraction;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Extraction;

[Trait("Category", "Unit")]
public class NupkgExtractorTests : IDisposable
{
    private readonly string _scratch;

    public NupkgExtractorTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), $"ppds-nupkg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Extract_NupkgMissingLibFolder_Throws()
    {
        // Build a minimal .nupkg-like zip with only a .nuspec at the root and no lib/ folder.
        var nupkgPath = Path.Combine(_scratch, "empty.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("empty.nuspec");
            using var entryStream = entry.Open();
            var xml = Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?>
                <package><metadata><id>empty</id></metadata></package>
                """);
            entryStream.Write(xml, 0, xml.Length);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.Contains("lib", ex.Message);
    }

    [Fact]
    public void Extract_NupkgWithZipSlipEntry_ThrowsPpdsException()
    {
        // Handcraft a zip archive that contains an entry whose full name traverses up out of the
        // extraction directory (classic zip-slip payload). .NET 8 blocks this by default inside
        // ExtractToDirectory, so the SUT's AssertExtractedEntriesContained walk is the second
        // line of defense. This test constructs the archive and confirms that either the platform
        // or the SUT refuses the package — both outcomes keep us safe.
        var nupkgPath = Path.Combine(_scratch, "evil.nupkg");
        using (var stream = File.Create(nupkgPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Benign nuspec for completeness.
            var nuspec = archive.CreateEntry("evil.nuspec");
            using (var entryStream = nuspec.Open())
            {
                var xml = Encoding.UTF8.GetBytes("""
                    <?xml version="1.0"?>
                    <package><metadata><id>evil</id></metadata></package>
                    """);
                entryStream.Write(xml, 0, xml.Length);
            }

            // Hostile entry — path escapes the temp dir via ".." segments.
            var hostile = archive.CreateEntry("../../escaped.txt");
            using var hostileStream = hostile.Open();
            var payload = Encoding.UTF8.GetBytes("pwned");
            hostileStream.Write(payload, 0, payload.Length);
        }

        // Either the platform raises IOException / InvalidDataException, or the SUT raises
        // PpdsException from AssertExtractedEntriesContained. Both are acceptable containment.
        var caught = Assert.ThrowsAny<Exception>(() => NupkgExtractor.Extract(nupkgPath));
        Assert.True(
            caught is PpdsException or IOException or InvalidDataException or UnauthorizedAccessException,
            $"Expected containment-related exception, got {caught.GetType().FullName}: {caught.Message}");
    }
}

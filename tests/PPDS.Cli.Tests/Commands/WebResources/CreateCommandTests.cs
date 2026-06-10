using System.CommandLine;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

/// <summary>
/// Parse-level and type-resolution tests for 'ppds webresources create' (#1207).
/// Covers AC-WR-56, AC-WR-57, AC-WR-58.
/// </summary>
[Trait("Category", "Unit")]
public class CreateCommandTests
{
    private readonly Command _command = CreateCommand.Create();

    [Fact]
    public void Create_CommandNameIsCreate()
    {
        Assert.Equal("create", _command.Name);
    }

    [Fact]
    public void Create_HasFileArgument()
    {
        var arg = _command.Arguments.FirstOrDefault(a => a.Name == "file");
        Assert.NotNull(arg);
    }

    [Theory]
    [InlineData("--name")]
    [InlineData("--display-name")]
    [InlineData("--type")]
    [InlineData("--solution")]
    [InlineData("--publish")]
    public void Create_HasOption(string optionName)
    {
        Assert.NotNull(_command.Options.FirstOrDefault(o => o.Name == optionName));
    }

    [Fact]
    public void Create_NameOptionIsRequired()
    {
        var opt = _command.Options.First(o => o.Name == "--name");
        Assert.True(opt.Required);
    }

    [Fact]
    public void Parse_ValidArgs_HasNoErrors()
    {
        var result = _command.Parse("icon.svg --name hsl_vet_icon.svg --display-name \"Vet Icon\" --type svg --solution PawsClawsLabs --publish");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_MissingName_HasErrors()
    {
        var result = _command.Parse("icon.svg");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_MissingFile_HasErrors()
    {
        var result = _command.Parse("--name hsl_vet_icon.svg");
        Assert.NotEmpty(result.Errors);
    }

    // ---------------------------------------------------------------------
    // ResolveType — extension inference + --type override (AC-WR-57)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("html", 1)]
    [InlineData("htm", 1)]
    [InlineData("css", 2)]
    [InlineData("js", 3)]
    [InlineData("xml", 4)]
    [InlineData("png", 5)]
    [InlineData("jpg", 6)]
    [InlineData("gif", 7)]
    [InlineData("xap", 8)]
    [InlineData("xsl", 9)]
    [InlineData("ico", 10)]
    [InlineData("svg", 11)]
    [InlineData("resx", 12)]
    public void ResolveType_InfersFromExtension(string extension, int expectedCode)
    {
        using var file = new TempFile($"resource.{extension}");

        var code = CreateCommand.ResolveType(file.Path, type: null);

        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void ResolveType_ExplicitTypeOverridesExtension()
    {
        using var file = new TempFile("resource.dat");

        var code = CreateCommand.ResolveType(file.Path, type: "png");

        Assert.Equal(5, code);
    }

    [Fact]
    public void ResolveType_Throws_WhenFileMissing()
    {
        var ex = Assert.Throws<PpdsException>(
            () => CreateCommand.ResolveType(@"Z:\does\not\exist.svg", type: null));

        Assert.Equal(ErrorCodes.Validation.FileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ResolveType_Throws_WhenExtensionUnknown_AndNoTypeGiven()
    {
        using var file = new TempFile("resource.dat");

        var ex = Assert.Throws<PpdsException>(
            () => CreateCommand.ResolveType(file.Path, type: null));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image")]
    [InlineData("data")]
    [InlineData("bogus")]
    public void ResolveType_Throws_WhenTypeIsNotASingleType(string type)
    {
        using var file = new TempFile("resource.svg");

        var ex = Assert.Throws<PpdsException>(
            () => CreateCommand.ResolveType(file.Path, type));

        Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
    }

    /// <summary>Disposable temp file with a controlled file name/extension.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(string fileName)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, fileName);
            File.WriteAllText(Path, "content");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of temp files
            }
        }
    }
}

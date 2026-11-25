using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

/// <summary>
/// The command's value path cannot run without an environment, so its wiring is pinned by its source (#1917): every
/// value comes from <c>IDataUpdateValueService</c>, and the command parses no value itself.
/// </summary>
[Trait("Category", "Unit")]
public class UpdateCommandValuePathTests
{
    private static string Source()
    {
        var location = Path.GetDirectoryName(typeof(UpdateCommandValuePathTests).Assembly.Location)!;
        var path = Path.GetFullPath(Path.Combine(
            location, "..", "..", "..", "..", "..", "src", "PPDS.Cli", "Commands", "Data", "UpdateCommand.cs"));
        Assert.True(File.Exists(path), $"UpdateCommand.cs not found from the test output ({path}).");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Set_and_file_values_come_from_the_value_service()
    {
        var source = Source();
        Assert.Contains("valueService.ConvertSetValuesAsync(entity, set!, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("valueService.ReadUpdateFileAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_beside_file_is_refused_instead_of_ignored()
    {
        var file = Path.GetTempFileName();
        try
        {
            var command = PPDS.Cli.Commands.Data.UpdateCommand.Create();
            var both = command.Parse(["--entity", "account", "--file", file, "--set", "name=x"]);
            Assert.Contains(both.Errors, e => e.Message.Contains("--set cannot be combined with --file", StringComparison.Ordinal));
            Assert.Empty(command.Parse(["--entity", "account", "--file", file]).Errors);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("TryParse(")]
    [InlineData(".Parse(")]
    [InlineData("File.ReadAllText")]
    public void The_command_parses_no_value_and_reads_no_file_itself(string call)
    {
        Assert.DoesNotContain(call, Source(), StringComparison.Ordinal);
    }
}

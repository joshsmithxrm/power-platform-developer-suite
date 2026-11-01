using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

[Collection(nameof(ConsoleCaptureCollection))]
public class CreateDiagnosticsTests
{
    [Fact]
    public async Task Create_WarningsAppearOnStderrAndInJsonWithoutChangingName()
    {
        var file = Path.Combine(Path.GetTempPath(), $"webresource-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(file, "// fixture");
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var id = Guid.NewGuid();
            var service = new Mock<IWebResourceService>(MockBehavior.Strict);
            service.Setup(s => s.CreateAsync(It.Is<CreateWebResourceRequest>(r => r.Name == "script.js"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CreateWebResourceResult(id, [new("WebResource.MissingPublisherPrefix", "Use '<prefix>_<name>'.")]));
            var exit = await CreateCommand.ExecuteAsync(file, "script.js", null, null, null, false,
                null, null, new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None,
                _ => Task.FromResult(new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider()));

            Assert.Equal(0, exit);
            Assert.Contains("Warning [WebResource.MissingPublisherPrefix]", stderr.ToString());
            using var json = JsonDocument.Parse(stdout.ToString());
            var data = json.RootElement.GetProperty("data");
            Assert.Equal("script.js", data.GetProperty("name").GetString());
            Assert.Equal(id, data.GetProperty("id").GetGuid());
            Assert.Equal("WebResource.MissingPublisherPrefix", data.GetProperty("warnings")[0].GetProperty("code").GetString());
            service.VerifyAll();
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            File.Delete(file);
        }
    }

    [Fact]
    public void Update_NotFoundHint_UsesLiteralFilePlaceholder()
    {
        Assert.Contains("ppds webresources create <file> --name new_script.js",
            UpdateCommand.DescribeMissingResource("new_script.js"));
    }
}

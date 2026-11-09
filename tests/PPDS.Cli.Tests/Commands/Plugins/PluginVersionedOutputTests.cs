using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class PluginVersionedOutputTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("assembly")]
    [InlineData("type")]
    [InlineData("step")]
    [InlineData("image")]
    public async Task Version2_UnknownFieldsRejectBeforeConnectionOrDefaultClears(string scope)
    {
        var root = JsonNode.Parse("""
            {"version":"2.0","assemblies":[{"name":"Fixture","path":"absent-do-not-open.dll","types":[{"typeName":"Fixture.Type","steps":[{"message":"Update","entity":"account","stage":"PostOperation","images":[{"name":"before","imageType":"PreImage"}]}]}]}]}
            """)!;
        var assembly = root["assemblies"]![0]!;
        var type = assembly["types"]![0]!;
        var step = type["steps"]![0]!;
        var target = scope switch
        {
            "assembly" => assembly, "type" => type, "step" => step,
            "image" => step["images"]![0]!, _ => root
        };
        target["filteringAttribute"] = "SYNTHETIC-UNKNOWN-VALUE";
        var path = Path.Combine(Path.GetTempPath(), $"ppds-unknown-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, root.ToJsonString());
        try
        {
            var connections = 0;
            using var output = new StringWriter();
            var exit = await DeployCommand.ExecuteAsync(new FileInfo(path), null, null, null,
                clean: false, dryRun: false, new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None, serviceProviderFactory: _ =>
                {
                    connections++;
                    throw new InvalidOperationException("Connection must remain unreachable.");
                }, outputWriter: new JsonOutputWriter(output));
            Assert.NotEqual(0, exit);
            Assert.Equal(0, connections);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("unsupported", document.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("absent-do-not-open", output.ToString());
            Assert.DoesNotContain("SYNTHETIC-UNKNOWN-VALUE", output.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData("2.0", false)]
    [InlineData("1.0", true)]
    [InlineData("2.0", true)]
    public async Task ActualDiff_RetainsArrayAndMakesIncompleteVerificationUnsuccessful(string version, bool secret)
    {
        const string sentinel = "SYNTHETIC-SECRET-OUTPUT-BOUNDARY";
        var service = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        PluginAssemblyInfo? missingAssembly = null;
        service.Setup(s => s.GetAssemblyByNameAsync("MissingFixture", It.IsAny<CancellationToken>()))
            .ReturnsAsync(missingAssembly);
        var step = new Dictionary<string, object?>
        {
            ["message"] = "Update", ["entity"] = "account", ["stage"] = "PostOperation"
        };
        if (secret) step["secureConfiguration"] = sentinel;
        var json = JsonSerializer.Serialize(new
        {
            version,
            assemblies = new[] { new { name = "MissingFixture", types = new[] { new { typeName = "Fixture.Type", steps = new[] { step } } } } }
        });
        var path = Path.Combine(Path.GetTempPath(), $"ppds-output-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var parse = DiffCommand.CreateForTesting(service.Object, output, error)
                .Parse(["--config", path, "--output-format", "json"]);
            Assert.Empty(parse.Errors);
            Assert.Equal(secret ? 2 : 1, await parse.InvokeAsync());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("1.0", document.RootElement.GetProperty("version").GetString());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("data").ValueKind);
            Assert.Equal(!secret, document.RootElement.GetProperty("success").GetBoolean());
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.Equal(secret ? "incomplete" : "drift", metadata.GetProperty("status").GetString());
            Assert.Equal(!secret, metadata.GetProperty("isComplete").GetBoolean());
            Assert.True(metadata.GetProperty("hasDrift").GetBoolean());
            Assert.DoesNotContain(sentinel, output.ToString() + error);
            service.VerifyAll();
            service.VerifyNoOtherCalls();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("9.0")]
    [InlineData("2")]
    [InlineData("SYNTHETIC-VERSION-SENTINEL")]
    public async Task UnsupportedVersion_DeployRejectsBeforeConnectionOrArtifactAccess(string version)
    {
        var json = JsonSerializer.Serialize(new
        {
            version, assemblies = new[] { new { name = "Fixture", path = "absent-do-not-open.dll", types = Array.Empty<object>() } }
        });
        var path = Path.Combine(Path.GetTempPath(), $"ppds-version-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            var connections = 0;
            using var output = new StringWriter();
            var exit = await DeployCommand.ExecuteAsync(new FileInfo(path), null, null, null,
                clean: false, dryRun: false, new GlobalOptionValues { OutputFormat = OutputFormat.Json },
                CancellationToken.None, serviceProviderFactory: _ =>
                {
                    connections++;
                    throw new InvalidOperationException("Connection must remain unreachable.");
                }, outputWriter: new JsonOutputWriter(output));
            Assert.NotEqual(0, exit);
            Assert.Equal(0, connections);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("Unsupported registration version", document.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.DoesNotContain("SYNTHETIC-VERSION-SENTINEL", output.ToString());
            Assert.DoesNotContain("absent-do-not-open", output.ToString());
        }
        finally { File.Delete(path); }
    }
}

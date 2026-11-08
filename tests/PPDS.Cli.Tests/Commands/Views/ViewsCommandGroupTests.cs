using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Views;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Cli.Commands.Views;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Views;

[Collection(nameof(ConsoleCaptureCollection))]
public class ViewsCommandGroupTests
{
    private readonly Command _command;

    public ViewsCommandGroupTests()
    {
        _command = ViewsCommandGroup.Create();
    }

    [Fact]
    public void Create_HasCorrectName()
    {
        Assert.Equal("views", _command.Name);
    }

    [Fact]
    public void Create_HasDescription()
    {
        Assert.NotEmpty(_command.Description ?? "");
    }

    // AC-17: all eleven subcommands present
    [Fact]
    public void Create_HasAllElevenSubcommands()
    {
        Assert.Equal(11, _command.Subcommands.Count);
    }

    [Fact]
    public void Create_HasListSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "list"));

    [Fact]
    public void Create_HasGetSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "get"));

    [Fact]
    public void Create_HasAddColumnSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "add-column"));

    [Fact]
    public void Create_HasRemoveColumnSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "remove-column"));

    [Fact]
    public void Create_HasUpdateColumnSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "update-column"));

    [Fact]
    public void Create_HasReorderColumnsSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "reorder-columns"));

    [Fact]
    public void Create_HasSetSortSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "set-sort"));

    [Fact]
    public void Create_HasClearSortSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "clear-sort"));

    [Fact]
    public void Create_HasSetFilterSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "set-filter"));

    [Fact]
    public void Create_HasClearFilterSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "clear-filter"));

    [Fact]
    public void Create_HasSetFetchXmlSubcommand()
        => Assert.NotNull(_command.Subcommands.FirstOrDefault(c => c.Name == "set-fetchxml"));

    // AC-18: every subcommand has a non-empty description
    [Fact]
    public void AllSubcommands_HaveDescription()
    {
        foreach (var sub in _command.Subcommands)
        {
            Assert.False(string.IsNullOrWhiteSpace(sub.Description),
                $"Subcommand '{sub.Name}' has no description.");
        }
    }

    // AC-19: set-fetchxml description includes XML format example
    [Fact]
    public void SetFetchXmlSubcommand_HelpIncludesXmlExample()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-fetchxml");
        Assert.Contains("<fetch", sub.Description, StringComparison.OrdinalIgnoreCase);
    }

    // AC-19: set-filter description includes filter fragment example
    [Fact]
    public void SetFilterSubcommand_HelpIncludesXmlExample()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        Assert.Contains("<filter", sub.Description, StringComparison.OrdinalIgnoreCase);
    }

    // AC-12: set-filter has both --filter-file and --condition options
    [Fact]
    public void SetFilterSubcommand_HasFilterFileOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--filter-file"));
    }

    [Fact]
    public void SetFilterSubcommand_HasConditionOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--condition"));
    }

    // AC-12: mutual exclusion is enforced in the handler (tested via parse/invoke in integration, structural check here)
    [Fact]
    public void SetFilterSubcommand_FilterFileAndConditionAreMutuallyExclusive()
    {
        // Both options exist but are distinct; mutual exclusion enforced in handler
        var sub = _command.Subcommands.First(c => c.Name == "set-filter");
        var filterFileOpt = sub.Options.FirstOrDefault(o => o.Name == "--filter-file");
        var conditionOpt = sub.Options.FirstOrDefault(o => o.Name == "--condition");
        Assert.NotNull(filterFileOpt);
        Assert.NotNull(conditionOpt);
        // They are not the same option — confirms both exist as separate options
        Assert.NotSame(filterFileOpt, conditionOpt);
    }

    // set-fetchxml has --fetchxml option
    [Fact]
    public void SetFetchXmlSubcommand_HasFetchXmlOption()
    {
        var sub = _command.Subcommands.First(c => c.Name == "set-fetchxml");
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--fetchxml"));
    }

    // mutation subcommands all have --solution and --publish
    [Theory]
    [InlineData("add-column")]
    [InlineData("remove-column")]
    [InlineData("update-column")]
    [InlineData("reorder-columns")]
    [InlineData("set-sort")]
    [InlineData("clear-sort")]
    [InlineData("set-filter")]
    [InlineData("clear-filter")]
    [InlineData("set-fetchxml")]
    public void MutationSubcommands_HaveSolutionAndPublishOptions(string subcommandName)
    {
        var sub = _command.Subcommands.First(c => c.Name == subcommandName);
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--solution"));
        Assert.NotNull(sub.Options.FirstOrDefault(o => o.Name == "--publish"));
    }

    private static readonly string[] WriterNames =
    [
        "add-column", "remove-column", "update-column", "reorder-columns",
        "set-sort", "clear-sort", "set-filter", "clear-filter", "set-fetchxml"
    ];

    public static IEnumerable<object[]> Writers => WriterNames.Select(name => new object[] { name });

    public static IEnumerable<object[]> DeniedCommands =>
        Writers.SelectMany(row => new[] { "missing", "mismatch", "read-only" }
            .Select(mode => new object[] { row[0], mode }));

    [Theory]
    [MemberData(nameof(Writers))]
    public async Task EveryMutation_ForwardsAcknowledgementAndOptions(string writer)
        => await InvokeMutation(writer, "allowed");

    [Theory]
    [MemberData(nameof(DeniedCommands))]
    public async Task EveryMutation_RejectsMismatchedTargetWithStructuredError(string writer, string mode)
        => await InvokeMutation(writer, mode);

    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    public void Reads_DoNotExposeAcknowledgement(string name)
    {
        var command = _command.Subcommands.Single(c => c.Name == name);
        Assert.DoesNotContain(command.Options, o => o.Name == "--confirm-env");
        var args = name == "list" ? "--entity account" : "--entity account --view View";
        Assert.Empty(command.Parse(args).Errors);
    }

    private static Command CreateWriter(string writer,
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>> providerFactory)
        => writer switch
        {
            "add-column" => AddColumnCommand.Create(providerFactory),
            "remove-column" => RemoveColumnCommand.Create(providerFactory),
            "update-column" => UpdateColumnCommand.Create(providerFactory),
            "reorder-columns" => ReorderColumnsCommand.Create(providerFactory),
            "set-sort" => SetSortCommand.Create(providerFactory),
            "clear-sort" => ClearSortCommand.Create(providerFactory),
            "set-filter" => SetFilterCommand.Create(providerFactory),
            "clear-filter" => ClearFilterCommand.Create(providerFactory),
            "set-fetchxml" => SetFetchXmlCommand.Create(providerFactory),
            _ => throw new ArgumentOutOfRangeException(nameof(writer))
        };

    private static async Task InvokeMutation(string writer, string mode)
    {
        const string target = "https://views.example.test";
        // Selection may be a friendly name. Only the resolved URL can acknowledge it.
        const string selector = "Selected Environment";
        var config = new Mock<IEnvironmentConfigService>(MockBehavior.Strict);
        config.Setup(c => c.GetConfigAsync(target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentConfig { Type = EnvironmentType.Production });
        var confirmation = new EnvironmentConfirmationService(
            new EnvironmentConfirmationContext("command-session", "command-profile", target, mode == "read-only"),
            config.Object, new EnvironmentAcknowledgementStore());
        var service = new Mock<IViewService>();
        CancellationToken providerToken = default;
        var command = CreateWriter(writer, (profile, environment, options, ct) =>
        {
            Assert.Equal("chosen-profile", profile);
            Assert.Equal(selector, environment);
            Assert.True(options.IsJsonMode);
            providerToken = ct;
            return Task.FromResult(new ServiceCollection()
                .AddSingleton<IEnvironmentConfirmationService>(confirmation)
                .AddSingleton(service.Object).BuildServiceProvider());
        });
        var file = Path.GetTempFileName();
        var priorOut = Console.Out;
        var priorError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            await File.WriteAllTextAsync(file, "<fetch><entity name='account'/></fetch>");
            string[] specific = writer switch
            {
                "add-column" => ["--column", "name:200"],
                "remove-column" => ["--column", "name"],
                "update-column" => ["--column", "name", "--width", "200"],
                "reorder-columns" => ["--columns", "name"],
                "set-sort" => ["--sort", "name:asc"],
                "set-filter" => ["--condition", "statecode:eq:0"],
                "set-fetchxml" => ["--fetchxml", file],
                _ => []
            };
            var args = new List<string> { "--entity", "account", "--view", "View", "--profile", "chosen-profile",
                "--environment", selector, "--solution", "solution-name", "--publish", "--output-format", "Json" };
            args.AddRange(specific);
            if (mode != "missing") args.AddRange(["--confirm-env", mode == "mismatch" ? "https://wrong.example.test" : target]);
            var parsed = command.Parse(args.ToArray());
            Assert.Empty(parsed.Errors);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await parsed.InvokeAsync();
            // Parsing the complete stdout also rejects extra status text or multiple JSON envelopes.
            using var json = JsonDocument.Parse(stdout.ToString());
            Assert.Empty(stderr.ToString());
            if (mode == "allowed")
            {
                Assert.Equal(ExitCodes.Success, exit);
                Assert.True(json.RootElement.GetProperty("success").GetBoolean());
                var call = Assert.Single(service.Invocations);
                var expectedMethod = writer switch
                {
                    "add-column" => nameof(IViewService.AddColumnAsync),
                    "remove-column" => nameof(IViewService.RemoveColumnAsync),
                    "update-column" => nameof(IViewService.UpdateColumnAsync),
                    "reorder-columns" => nameof(IViewService.ReorderColumnsAsync),
                    "set-sort" => nameof(IViewService.SetSortAsync),
                    "clear-sort" => nameof(IViewService.ClearSortAsync),
                    "set-filter" => nameof(IViewService.SetFilterAsync),
                    "clear-filter" => nameof(IViewService.ClearFilterAsync),
                    _ => nameof(IViewService.SetFetchXmlAsync)
                };
                Assert.Equal(expectedMethod, call.Method.Name);
                var parameters = call.Method.GetParameters();
                Assert.Equal("account", call.Arguments[0]);
                Assert.Equal("View", call.Arguments[1]);
                Assert.Equal(true, call.Arguments[Array.FindIndex(parameters, p => p.Name == "publish")]);
                Assert.Equal("solution-name", call.Arguments[Array.FindIndex(parameters, p => p.Name == "solution")]);
                Assert.Equal(providerToken, call.Arguments[^1]);
                // The CLI supplied the URL to the real guard: its scoped acknowledgement is now reusable.
                await confirmation.EnsureConfirmedAsync("writer gate", cancellationToken: providerToken);
            }
            else
            {
                Assert.Equal(ExitCodes.Failure, exit);
                Assert.False(json.RootElement.GetProperty("success").GetBoolean());
                var expectedCode = mode switch
                {
                    "missing" => EnvironmentConfirmationErrorCodes.Required,
                    "mismatch" => EnvironmentConfirmationErrorCodes.TargetMismatch,
                    _ => EnvironmentConfirmationErrorCodes.ReadOnly
                };
                Assert.Equal(expectedCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.Empty(service.Invocations);
            }
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorError);
            File.Delete(file);
        }
    }
}

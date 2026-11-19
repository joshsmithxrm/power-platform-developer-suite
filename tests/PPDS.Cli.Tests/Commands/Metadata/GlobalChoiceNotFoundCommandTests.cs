using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// #1759: every command whose target is a global choice reports a name that exists in no spelling as
/// <c>Operation.NotFound</c> with the not-found exit code (6). The authoring commands run the real service over a
/// fake Dataverse that rejects the name with the ObjectDoesNotExist fault; <c>get</c> runs over a query service that
/// reports the name absent (the query service itself is covered in PPDS.Dataverse.Tests).
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class GlobalChoiceNotFoundCommandTests
{
    private const string Missing = "mayo_Missing";

    public static TheoryData<string> AuthoringCommands => new()
    {
        $"optionset update --solution Sol --name {Missing} --display-name X",
        $"optionset add-option --solution Sol --name {Missing} --label X",
        $"optionset update-option --solution Sol --name {Missing} --value 1 --new-label X",
        $"optionset remove-option --solution Sol --name {Missing} --value 1 --force",
        $"optionset delete --solution Sol --name {Missing} --force",
        $"optionset delete --solution Sol --name {Missing} --dry-run"
    };

    [Theory]
    [MemberData(nameof(AuthoringCommands))]
    [Trait("AC", "AC-134")]
    public async Task Json_UnknownChoice_IsOperationNotFound_Exit6(string command)
    {
        using var fake = new GlobalChoiceServerFake();
        var (exit, stdout, _) = await Invoke(command + " --output-format json", fake.Service, query: null);

        Assert.Equal(ExitCodes.NotFoundError, exit);
        Assert.Equal(6, exit);
        AssertNotFoundEnvelope(stdout);
    }

    [Theory]
    [InlineData("optionset update --solution Sol --name mayo_Missing --display-name X")]
    [InlineData("optionset delete --solution Sol --name mayo_Missing --force")]
    [Trait("AC", "AC-134")]
    public async Task Text_UnknownChoice_NamesTheChoice_Exit6(string command)
    {
        using var fake = new GlobalChoiceServerFake();
        var (exit, _, stderr) = await Invoke(command, fake.Service, query: null);

        Assert.Equal(ExitCodes.NotFoundError, exit);
        Assert.Contains(Missing, stderr);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("AC", "AC-134")]
    public async Task Get_UnknownChoice_IsNotFound_Exit6_InTextAndJson(bool json)
    {
        var query = new Mock<IMetadataQueryService>(MockBehavior.Strict);
        query.Setup(q => q.GetOptionSetAsync(Missing, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OptionSetNotFoundException(Missing, new InvalidOperationException("synthetic fault")));

        var (exit, stdout, stderr) = await Invoke($"optionset {Missing}" + (json ? " --output-format json" : ""), authoring: null, query.Object);

        Assert.Equal(ExitCodes.NotFoundError, exit);
        if (json)
            AssertNotFoundEnvelope(stdout);
        else
            Assert.Contains(Missing, stderr);
    }

    [Fact]
    [Trait("AC", "AC-134")]
    public async Task Get_OtherFailure_StillReportsTheOriginalClassification()
    {
        // Only a confirmed-absent name is not-found: any other failure keeps its own error code and the failure exit.
        var query = new Mock<IMetadataQueryService>(MockBehavior.Strict);
        query.Setup(q => q.GetOptionSetAsync(Missing, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

        var (exit, stdout, _) = await Invoke($"optionset {Missing} --output-format json", authoring: null, query.Object);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(ErrorCodes.Operation.Internal, ErrorCode(stdout, namesTheChoice: false));
    }

    private static void AssertNotFoundEnvelope(string stdout)
        => Assert.Equal(ErrorCodes.Operation.NotFound, ErrorCode(stdout));

    private static string ErrorCode(string stdout, bool namesTheChoice = true)
    {
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        var error = doc.RootElement.GetProperty("error");
        if (namesTheChoice) Assert.Contains(Missing, error.GetProperty("message").GetString() ?? "");
        return error.GetProperty("code").GetString()!;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Invoke(
        string command, IMetadataAuthoringService? authoring, IMetadataQueryService? query)
    {
        Task<ServiceProvider> Provider(CancellationToken _)
        {
            var services = new ServiceCollection()
                .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" });
            if (authoring != null) services.AddSingleton(authoring);
            if (query != null) services.AddSingleton(query);
            return Task.FromResult(services.BuildServiceProvider());
        }

        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(command.Split(' '));
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
}

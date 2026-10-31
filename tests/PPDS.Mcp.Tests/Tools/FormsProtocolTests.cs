using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Models;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>Real MCP JSON-RPC transport with an isolated application-service fixture; no Dataverse access.</summary>
public sealed class FormsProtocolTests
{
    [Fact]
    public async Task ClientListsBoundedTools_AndCallsReadDraftPublishJourneyWithProgress()
    {
        var formId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var hash = new string('a', 64);
        var progressReceived = new TaskCompletionSource<ProgressNotificationValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forms = new Mock<IFormService>();
        forms.Setup(f => f.ListAsync("fixture_table", It.IsAny<CancellationToken>())).ReturnsAsync(new ListResult<FormInfo>
        {
            Items = [new(formId, "Fixture", 2, "Main", true, null)], TotalCount = 1
        });
        forms.Setup(f => f.GetAsync("fixture_table", formId.ToString(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FormDetail(formId, "Fixture", 2, "Main", true, null, [], "<form/>", hash, "unpublished"));
        AddFieldDraftRequest? draft = null;
        PublishFormEntityRequest? publish = null;
        forms.Setup(f => f.AddFieldDraftAsync(It.IsAny<AddFieldDraftRequest>(), It.IsAny<IProgressReporter>(), It.IsAny<CancellationToken>()))
            .Returns<AddFieldDraftRequest, IProgressReporter, CancellationToken>(async (request, progress, ct) =>
            {
                draft = request; progress.ReportPhase("Verifying", "Read back draft");
                // Keep simulated work active until delivery. IProgress sends asynchronously; completing
                // immediately can legitimately race the SDK removing its per-call progress callback.
                await progressReceived.Task.WaitAsync(ct);
                return new FormAuthoringResult { EnvironmentUrl = "https://fixture.example.test", EntityLogicalName = request.EntityLogicalName,
                    FormId = request.FormId, Status = "saved-unpublished", DraftHash = hash, DraftWrite = new("succeeded"), DraftVerification = new("succeeded") };
            });
        forms.Setup(f => f.PublishFormEntityAsync(It.IsAny<PublishFormEntityRequest>(), It.IsAny<IProgressReporter>(), It.IsAny<CancellationToken>()))
            .Returns<PublishFormEntityRequest, IProgressReporter, CancellationToken>((request, _, _) =>
            {
                publish = request;
                return Task.FromResult(new FormAuthoringResult { EnvironmentUrl = "https://fixture.example.test", EntityLogicalName = request.EntityLogicalName,
                    FormId = request.FormId, Status = "published", PublicationScope = "entity", EntityPublication = new("succeeded"), PublishedVerification = new("succeeded") });
            });
        await using var fixture = await Fixture.CreateAsync(forms.Object);
        var tools = await fixture.Client.ListToolsAsync(cancellationToken: fixture.Token);
        tools.Select(t => t.Name).Should().BeEquivalentTo("ppds_forms_list", "ppds_forms_get", "ppds_forms_add_field", "ppds_forms_publish");
        tools.Single(t => t.Name == "ppds_forms_add_field").JsonSchema.ToString().Should().NotContain("progress").And.NotContain("formXml");
        var list = await fixture.CallAsync("ppds_forms_list", new() { ["entityLogicalName"] = "fixture_table" });
        list.Should().Contain(formId.ToString()).And.Contain("https://fixture.example.test");
        var get = await fixture.CallAsync("ppds_forms_get", new() { ["entityLogicalName"] = "fixture_table", ["formId"] = formId, ["unpublished"] = true });
        get.Should().Contain(hash).And.Contain("unpublished");
        var add = await fixture.Client.CallToolAsync("ppds_forms_add_field", new Dictionary<string, object?>
        {
            ["entityLogicalName"] = "fixture_table", ["formId"] = formId, ["sectionId"] = sectionId,
            ["fieldLogicalName"] = "fixture_value", ["expectedDraftHash"] = hash, ["confirmEnvironment"] = "https://fixture.example.test"
        }, progress: new InlineProgress(value => progressReceived.TrySetResult(value)), cancellationToken: fixture.Token);
        add.IsError.Should().BeFalse();
        JsonSerializer.Serialize(add).Should().Contain("saved-unpublished");
        (await progressReceived.Task.WaitAsync(fixture.Token)).Message.Should().Contain("Verifying");
        draft.Should().NotBeNull();
        draft!.SectionId.Should().Be(sectionId);
        draft.ConfirmEnvironment.Should().Be("https://fixture.example.test");
        var publication = await fixture.CallAsync("ppds_forms_publish", new()
        {
            ["entityLogicalName"] = "fixture_table", ["formId"] = formId,
            ["expectedDraftHash"] = hash, ["confirmPublishEntity"] = "fixture_table"
        });
        publication.Should().Contain("published").And.Contain("entity");
        publish!.ConfirmPublishEntity.Should().Be("fixture_table");
    }

    [Fact]
    public async Task ClientReceivesStructuredGuardError_AndInvalidInputNeverCallsService()
    {
        var forms = new Mock<IFormService>(MockBehavior.Strict);
        forms.Setup(f => f.AddFieldDraftAsync(It.IsAny<AddFieldDraftRequest>(), It.IsAny<IProgressReporter>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException("Environment.ConfirmationRequired", "Confirm https://fixture.example.test"));
        await using var fixture = await Fixture.CreateAsync(forms.Object);
        var response = await fixture.Client.CallToolAsync("ppds_forms_add_field", new Dictionary<string, object?>
        {
            ["entityLogicalName"] = "fixture_table", ["formId"] = Guid.NewGuid(), ["sectionId"] = Guid.NewGuid(),
            ["fieldLogicalName"] = "fixture_value", ["expectedDraftHash"] = new string('a', 64)
        }, cancellationToken: fixture.Token);
        response.IsError.Should().BeTrue();
        JsonSerializer.Serialize(response).Should().Contain("Environment.ConfirmationRequired");
        var invalid = await fixture.Client.CallToolAsync("ppds_forms_list", new Dictionary<string, object?> { ["entityLogicalName"] = " " }, cancellationToken: fixture.Token);
        invalid.IsError.Should().BeTrue();
        forms.Verify(f => f.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class InlineProgress(Action<ProgressNotificationValue> callback) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => callback(value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-forms-protocol-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
        private ProfileStore _store = null!;
        private McpToolContext _context = null!;
        private IMcpServer _server = null!;
        private Task _serverTask = null!;
        public IMcpClient Client { get; private set; } = null!;
        public CancellationToken Token => _lifetime.Token;
        public static async Task<Fixture> CreateAsync(IFormService forms)
        {
            var f = new Fixture();
            Directory.CreateDirectory(f._directory);
            f._store = new(Path.Combine(f._directory, "profiles.json"));
            await f._store.SaveAsync(new() { ActiveProfileIndex = 1, Profiles = [new() { Index = 1, Name = "fixture", Environment = new() { Url = "https://fixture.example.test" } }] });
            f._context = new(Mock.Of<IMcpConnectionPoolManager>(), f._store, Mock.Of<ISecureCredentialStore>(), new())
            {
                ServiceProviderFactory = (_, target, _, _) =>
                {
                    var confirmation = new Mock<IEnvironmentConfirmationService>();
                    confirmation.SetupGet(c => c.Context).Returns(target);
                    return new ServiceCollection().AddSingleton(forms).AddSingleton(confirmation.Object).BuildServiceProvider();
                }
            };
            await f._context.InitializeAsync();
            var instance = new FormsTools(f._context);
            var collection = new McpServerPrimitiveCollection<McpServerTool>();
            foreach (var method in typeof(FormsTools).GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
                collection.Add(McpServerTool.Create(method, instance));
            var requests = new Pipe();
            var responses = new Pipe();
            f._server = McpServerFactory.Create(new StreamServerTransport(requests.Reader.AsStream(), responses.Writer.AsStream()),
                new() { ServerInfo = new() { Name = "forms-protocol-test", Version = "1" }, Capabilities = new() { Tools = new() { ToolCollection = collection } } });
            f._serverTask = f._server.RunAsync(f.Token);
            f.Client = await McpClientFactory.CreateAsync(new StreamClientTransport(requests.Writer.AsStream(), responses.Reader.AsStream()), cancellationToken: f.Token);
            return f;
        }
        public async Task<string> CallAsync(string name, Dictionary<string, object?> arguments)
        {
            var response = await Client.CallToolAsync(name, arguments, cancellationToken: Token);
            response.IsError.Should().BeFalse();
            return JsonSerializer.Serialize(response);
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _lifetime.Cancel();
            try { await _serverTask; } catch (OperationCanceledException) { }
            await _server.DisposeAsync();
            _context.Dispose();
            _store.Dispose();
            _lifetime.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}

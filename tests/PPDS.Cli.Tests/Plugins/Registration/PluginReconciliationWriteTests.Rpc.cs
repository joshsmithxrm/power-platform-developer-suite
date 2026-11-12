using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using Xunit;
using System.Text;
using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace PPDS.Cli.Tests.Plugins.Registration;

public partial class PluginReconciliationWriteTests
{
    [Theory]
    [InlineData(ErrorCodes.Plugin.UpdateNotPersisted)]
    [InlineData(ErrorCodes.Plugin.VerificationFailed)]
    public async Task RpcUpdateStep_VerificationFailureIsNonSuccess(string errorCode)
    {
        var stepId = Guid.NewGuid();
        var safeMessage = errorCode == ErrorCodes.Plugin.UpdateNotPersisted
            ? $"Dataverse acknowledged the update to step '{stepId}', but the fresh invocationSource read returned 1 instead of 0. No retry or rollback was attempted."
            : $"Dataverse acknowledged the update to step '{stepId}', but invocationSource persistence could not be verified. No retry or rollback was attempted.";
        const string privateMarker = "PRIVATE-READ-FAILURE";
        using var cancellation = new CancellationTokenSource();
        var failure = new PpdsException(errorCode, safeMessage, new InvalidOperationException(privateMarker));
        failure.Data["private-diagnostic"] = privateMarker;
        var mutations = new PluginMutationJournal();
        await mutations.RunAsync("sdkmessageprocessingstep", "update", "row", stepId,
            () => Task.CompletedTask, cancellation.Token);
        mutations.Attach(failure);
        var acknowledged = Assert.Single(PluginMutationJournal.Read(failure));
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.UpdateStepAsync(stepId,
                It.Is<StepUpdateRequest>(request => request.InvocationSource == "Parent"), cancellation.Token))
            .ThrowsAsync(failure);
        using var fixture = await RpcFixture.CreateAsync(registration.Object);

        var error = await Assert.ThrowsAsync<RpcException>(() => fixture.Handler.PluginsUpdateStepAsync(
            stepId.ToString(), invocationSource: "Parent", cancellationToken: cancellation.Token));

        Assert.Equal(errorCode, error.StructuredErrorCode);
        Assert.Equal(safeMessage, error.Message);
        Assert.DoesNotContain(privateMarker, error.Message);
        Assert.DoesNotContain(privateMarker, JsonSerializer.Serialize(error.ErrorData));
        Assert.Null(error.InnerException);
        Assert.False(error.Data.Contains("private-diagnostic"));
        Assert.Equal(acknowledged, Assert.Single(PluginMutationJournal.Read(error)));
        Assert.Equal("acknowledged", acknowledged.Outcome);
        Assert.Equal(stepId, acknowledged.RecordId);
        registration.VerifyAll();
        Assert.Single(registration.Invocations);
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcUpdateStep_CancelledVerificationPreservesExceptionTokenAndAcknowledgement()
    {
        var stepId = Guid.NewGuid();
        const string safeMessage = "Invocation-source verification was cancelled after Dataverse acknowledged the step update; persistence remains unverified. No retry or rollback was attempted.";
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(safeMessage,
            new InvalidOperationException("PRIVATE-CANCELLATION-CAUSE"), cancellation.Token);
        var mutations = new PluginMutationJournal();
        await mutations.RunAsync("sdkmessageprocessingstep", "update", "row", stepId,
            () => Task.CompletedTask, cancellation.Token);
        mutations.Attach(failure);
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.UpdateStepAsync(stepId,
                It.Is<StepUpdateRequest>(request => request.InvocationSource == "Parent"), cancellation.Token))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(failure);
        using var fixture = await RpcFixture.CreateAsync(registration.Object);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Handler.PluginsUpdateStepAsync(
            stepId.ToString(), invocationSource: "Parent", cancellationToken: cancellation.Token));

        Assert.Same(failure, error);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(safeMessage, error.Message);
        Assert.Equal(Assert.Single(PluginMutationJournal.Read(failure)), Assert.Single(PluginMutationJournal.Read(error)));
        Assert.Equal("acknowledged", PluginMutationJournal.Read(error)[0].Outcome);
        Assert.Equal(stepId, PluginMutationJournal.Read(error)[0].RecordId);
        registration.VerifyAll();
        Assert.Single(registration.Invocations);
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcUpdateStep_CancelledWireResponseDoesNotExposeMutationReceipt()
    {
        var stepId = Guid.NewGuid();
        const string safeMessage = "Invocation-source verification was cancelled after Dataverse acknowledged the step update; persistence remains unverified. No retry or rollback was attempted.";
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(safeMessage,
            new InvalidOperationException("PRIVATE-CANCELLATION-CAUSE"), cancellation.Token);
        var mutations = new PluginMutationJournal();
        await mutations.RunAsync("sdkmessageprocessingstep", "update", "row", stepId,
            () => Task.CompletedTask, cancellation.Token);
        mutations.Attach(failure);
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.UpdateStepAsync(stepId,
                It.Is<StepUpdateRequest>(request => request.InvocationSource == "Parent"), It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(failure);
        using var fixture = await RpcFixture.CreateAsync(registration.Object);
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = clientStream;
        using var server = serverStream;
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(server, server, new SystemTextJsonFormatter()));
        rpc.AddLocalRpcTarget(fixture.Handler);
        rpc.StartListening();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0", id = 1, method = "plugins/updateStep",
            @params = new { id = stepId.ToString(), invocationSource = "Parent" }
        });
        await client.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), timeout.Token);
        await client.WriteAsync(payload, timeout.Token);
        await client.FlushAsync(timeout.Token);

        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await client.ReadExactlyAsync(one, timeout.Token);
            header.Append((char)one[0]);
            Assert.True(header.Length < 4096);
        }
        var lengthLine = header.ToString().Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var response = new byte[int.Parse(lengthLine.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)];
        await client.ReadExactlyAsync(response, timeout.Token);
        using var json = JsonDocument.Parse(response);
        Assert.False(json.RootElement.TryGetProperty("result", out _));
        Assert.Equal(-32800, json.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("acknowledged", Encoding.UTF8.GetString(response));
        Assert.DoesNotContain("PRIVATE-CANCELLATION-CAUSE", Encoding.UTF8.GetString(response));
        Assert.DoesNotContain(stepId.ToString(), Encoding.UTF8.GetString(response));
        Assert.Equal("acknowledged", Assert.Single(PluginMutationJournal.Read(failure)).Outcome);
        registration.VerifyAll();
        Assert.Single(registration.Invocations);
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    [Theory]
    [InlineData("registerStep")]
    [InlineData("registerImage")]
    [InlineData("updateStep")]
    [InlineData("updateImage")]
    public async Task RpcLegacyPositionalArray_PreservesExplicitTargetAndProfile(string method)
    {
        const string target = "https://explicit-target.invalid";
        const string profile = "explicit-profile";
        using var fixture = await RpcFixture.CreateAsync(_service, target, profile);
        object?[] args = method switch
        {
            "registerStep" => [_existing.Id.ToString(), "Update", "account", "PostOperation", "Synchronous", "pluginType", 1, null, "step", null, null, null, null, null, null, null, null, false, null, null, target, profile],
            "registerImage" => [_existing.Id.ToString(), "pre", "PreImage", "name", "pre", "Update", target, profile],
            "updateStep" => [_existing.Id.ToString(), null, null, null, null, "updated", null, null, null, target, profile],
            "updateImage" => [_existing.Id.ToString(), "name", "pre", target, profile],
            _ => throw new InvalidOperationException()
        };
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = clientStream;
        using var server = serverStream;
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(server, server, new SystemTextJsonFormatter()));
        rpc.AddLocalRpcTarget(fixture.Handler);
        rpc.StartListening();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, method = "plugins/" + method, @params = args });
        await client.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n"), timeout.Token);
        await client.WriteAsync(payload, timeout.Token);
        await client.FlushAsync(timeout.Token);

        var header = new StringBuilder(); var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await client.ReadExactlyAsync(one, timeout.Token);
            header.Append((char)one[0]);
            Assert.True(header.Length < 4096);
        }
        var lengthLine = header.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var response = new byte[int.Parse(lengthLine.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)];
        await client.ReadExactlyAsync(response, timeout.Token);
        using var json = JsonDocument.Parse(response);
        Assert.False(json.RootElement.TryGetProperty("error", out _), Encoding.UTF8.GetString(response));
        Assert.Equal(_existing.Id.ToString(), json.RootElement.GetProperty("result").GetProperty("id").GetString());
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcUpdateStep_EmptyStageAndModePreservePreOperationWithoutWrites()
    {
        _existing["stage"] = new OptionSetValue(20);
        _existing["mode"] = new OptionSetValue(0);
        using var fixture = await RpcFixture.CreateAsync(_service);
        await fixture.Handler.PluginsUpdateStepAsync(_existing.Id.ToString(), stage: "", mode: "");
        Assert.Empty(_writes);
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcRegisterImage_EmptyLegacyOverridesPreserveExistingValues()
    {
        using var fixture = await RpcFixture.CreateAsync(_service);
        await fixture.Handler.PluginsRegisterImageAsync(Guid.NewGuid().ToString(), "pre", "PreImage",
            attributes: "name", entityAlias: "", messageName: "Update", messagePropertyName: "");
        var write = Assert.Single(_writes);
        Assert.Equal("name", write["attributes"]);
        Assert.False(write.Contains("entityalias"));
        Assert.False(write.Contains("messagepropertyname"));
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcRegisterStep_EmptyLegacyModeCreatesWithSynchronousDefault()
    {
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase query, CancellationToken _) =>
            {
                var name = ((QueryExpression)query).EntityName;
                return name == "sdkmessageprocessingstep" ? new EntityCollection()
                    : new EntityCollection(new List<Entity> { new(name, Guid.NewGuid()) });
            });
        Entity? created = null;
        _client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => created = e).ReturnsAsync(Guid.NewGuid());
        using var fixture = await RpcFixture.CreateAsync(_service);
        await fixture.Handler.PluginsRegisterStepAsync(Guid.NewGuid().ToString(), "Update", "account", "PreOperation", mode: "");
        Assert.NotNull(created);
        Assert.Equal(0, created.GetAttributeValue<OptionSetValue>("mode").Value);
        Assert.Equal(20, created.GetAttributeValue<OptionSetValue>("stage").Value);
        fixture.AssertNoProfileSaved();
    }

    // Explicit private path, cached synthetic metadata only; no saved profiles or credential provider.
    private sealed class RpcFixture : IDisposable
    {
        private readonly string _path;
        private readonly ServiceProvider _auth;
        private readonly ServiceProvider _services;
        private readonly Mock<IDaemonConnectionPoolManager> _daemon;
        public RpcMethodHandler Handler { get; }
        private RpcFixture(string path, ProfileStore store, IPluginRegistrationService registration, string target, string profile)
        {
            _path = path;
            _auth = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
            _services = new ServiceCollection().AddSingleton(registration).BuildServiceProvider();
            _daemon = new Mock<IDaemonConnectionPoolManager>(MockBehavior.Strict);
            _daemon.Setup(p => p.GetOrCreateServiceProviderAsync(It.Is<IReadOnlyList<string>>(names => names.Count == 1 && names[0] == profile),
                target, It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(_services);
            Handler = new RpcMethodHandler(_daemon.Object, _auth);
        }
        public static async Task<RpcFixture> CreateAsync(IPluginRegistrationService registration, string target = "https://offline.invalid", string profile = "offline")
        {
            var path = Path.Combine(Path.GetTempPath(), "ppds-rpc-metadata-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.False(File.Exists(path));
            var store = new ProfileStore(path);
            var metadata = await store.LoadAsync(); // File is absent; initializes only the cached collection.
            metadata.Add(new AuthProfile { Name = "offline", Environment = new PPDS.Auth.Profiles.EnvironmentInfo { Url = "https://offline.invalid" } }, setAsActive: true);
            if (profile != "offline")
                metadata.Add(new AuthProfile { Name = profile, Environment = new PPDS.Auth.Profiles.EnvironmentInfo { Url = "https://saved-profile-default.invalid" } }, setAsActive: false);
            return new RpcFixture(path, store, registration, target, profile);
        }
        public void AssertTargetUsedOnce() => _daemon.Verify(p => p.GetOrCreateServiceProviderAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<Action<DeviceCodeInfo>?>(), It.IsAny<CancellationToken>()), Times.Once);
        public void AssertNoProfileSaved() => Assert.False(File.Exists(_path));
        public void Dispose() { AssertNoProfileSaved(); Handler.Dispose(); _services.Dispose(); _auth.Dispose(); }
    }
}

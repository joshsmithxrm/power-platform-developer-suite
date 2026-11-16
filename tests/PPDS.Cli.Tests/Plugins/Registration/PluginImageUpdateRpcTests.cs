using System.Text;
using System.Text.Json;
using Moq;
using Nerdbank.Streams;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using StreamJsonRpc;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public partial class PluginReconciliationWriteTests
{
    [Theory]
    [InlineData(ErrorCodes.Plugin.ImageUpdateConflict, "The image changed after it was read. The conditional update was rejected; review its current values before submitting another update.")]
    [InlineData(ErrorCodes.Plugin.FieldCapabilityUnknown, "Image metadata does not establish optimistic concurrency support. No image update was dispatched.")]
    [InlineData(ErrorCodes.Plugin.FieldNotWritable, "Image metadata does not permit updating 'entityalias'. No image update was dispatched.")]
    [InlineData(ErrorCodes.Plugin.VerificationFailed, "Dataverse did not return an image update acknowledgement. The outcome is uncertain; PPDS will not retry automatically.")]
    [InlineData(ErrorCodes.Operation.PartialFailure, "Dataverse acknowledged the image update, but returning the connection failed. Do not repeat the update solely because of this cleanup failure.")]
    [InlineData(ErrorCodes.Operation.Internal, "The conditional image update failed and its outcome is unverified. No automatic mutation retry was attempted; verify the current image before another update.")]
    public async Task RpcUpdateImage_WirePreservesSafeOutcomeWithoutPrivateDiagnostics(string errorCode, string safeMessage)
    {
        var imageId = Guid.NewGuid();
        const string privateMarker = "PRIVATE-IMAGE-DIAGNOSTIC";
        var failure = new PpdsException(errorCode, privateMarker,
            new Dictionary<string, object> { ["connection"] = privateMarker },
            new InvalidOperationException(privateMarker)) { UserMessage = safeMessage };
        failure.Data["diagnostic"] = privateMarker;
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.UpdateImageAsync(imageId,
                It.Is<ImageUpdateRequest>(request => request.Name == "Renamed"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        using var fixture = await RpcFixture.CreateAsync(registration.Object);

        using var response = await InvokeImageUpdateWireAsync(fixture.Handler, imageId);

        Assert.False(response.RootElement.TryGetProperty("result", out _));
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(safeMessage, error.GetProperty("message").GetString());
        var data = error.GetProperty("data");
        Assert.Equal(errorCode, data.GetProperty("code").GetString());
        Assert.Equal(safeMessage, data.GetProperty("message").GetString());
        Assert.DoesNotContain(privateMarker, response.RootElement.GetRawText());
        registration.VerifyAll();
        Assert.Single(registration.Invocations);
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcUpdateImage_UnexpectedWireFailureRemainsRedacted()
    {
        var imageId = Guid.NewGuid();
        const string privateMarker = "PRIVATE-UNEXPECTED-IMAGE-FAILURE";
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.UpdateImageAsync(imageId,
                It.Is<ImageUpdateRequest>(request => request.Name == "Renamed"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(privateMarker));
        using var fixture = await RpcFixture.CreateAsync(registration.Object);

        using var response = await InvokeImageUpdateWireAsync(fixture.Handler, imageId);

        Assert.False(response.RootElement.TryGetProperty("result", out _));
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(ErrorCodes.Operation.Internal, error.GetProperty("data").GetProperty("code").GetString());
        Assert.Contains("CorrelationId", error.GetProperty("message").GetString());
        Assert.DoesNotContain(privateMarker, response.RootElement.GetRawText());
        registration.VerifyAll();
        Assert.Single(registration.Invocations);
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    [Fact]
    public async Task RpcUpdateImage_CancellationPreservesOriginalToken()
    {
        var imageId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException("Image update cancelled", cancellation.Token);
        var registration = new Mock<IPluginRegistrationService>(MockBehavior.Strict);
        registration.Setup(service => service.UpdateImageAsync(imageId,
                It.Is<ImageUpdateRequest>(request => request.Name == "Renamed"), cancellation.Token))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(failure);
        using var fixture = await RpcFixture.CreateAsync(registration.Object);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PluginsUpdateImageAsync(imageId.ToString(), name: "Renamed", cancellationToken: cancellation.Token));

        Assert.Same(failure, error);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        registration.VerifyAll();
        Assert.Single(registration.Invocations);
        fixture.AssertTargetUsedOnce();
        fixture.AssertNoProfileSaved();
    }

    private static async Task<JsonDocument> InvokeImageUpdateWireAsync(RpcMethodHandler handler, Guid imageId)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = clientStream;
        using var server = serverStream;
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(server, server, new SystemTextJsonFormatter()));
        rpc.AddLocalRpcTarget(handler);
        rpc.StartListening();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0", id = 1, method = "plugins/updateImage",
            @params = new { id = imageId.ToString(), name = "Renamed" }
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
        var lengthLine = header.ToString().Split("\r\n")
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(lengthLine.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(length, 1, 16384);
        var response = new byte[length];
        await client.ReadExactlyAsync(response, timeout.Token);
        return JsonDocument.Parse(response);
    }
}

using PPDS.Auth.Credentials;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;
using Xunit;
using PPDS.Tests.Shared;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

public sealed partial class MetadataLeaseLifetimeTests
{
    public static IEnumerable<object[]> ColorCancellationCases()
    {
        foreach (var (route, stage) in new[] { ("global-add", "global-color-read"), ("column-add", "local-color-read") })
        foreach (var delayed in new[] { false, true })
            yield return [route, stage, delayed];
    }

    /// <summary>
    /// AC-97 amended by AC-146: the colour read-back runs after the committed write, so its cancellation (immediate or
    /// delayed) is reported as WRITE_NOT_CONFIRMED carrying the exact cancellation exception and token as the cause,
    /// stops later work and returns every lease.
    /// </summary>
    [Theory, MemberData(nameof(ColorCancellationCases)), Trait("AC", "AC-97")]
    public async Task ErrorContract_ColorCancellation_IsWriteNotConfirmedCarryingTheCancellation(string route, string stage, bool delayed)
    {
        using var h = new Harness(route, true, true, stage, "cancel", delayFault: delayed);
        var operation = h.Run();
        try
        {
            if (delayed)
            {
                await HangBackstop.WaitAsync(h.FaultReached.Task, "the operation to reach the delayed fault");
                Assert.False(operation.IsCompleted);
                h.ReleaseFault();
            }
            var error = await Assert.ThrowsAsync<PpdsException>(() => operation);
            Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
            var cause = Assert.IsType<OperationCanceledException>(error.InnerException);
            Assert.Same(h.Injected, cause);
            Assert.Equal(h.Token, cause.CancellationToken);
            Assert.Contains("the read was cancelled", error.Message);
            Assert.True(operation.IsFaulted);
            AssertCliEnvelope(error, MetadataErrorCodes.WriteNotConfirmed, 2);
            var failedIndex = Array.IndexOf(h.ExpectedStages, stage);
            h.AssertFinished(h.ExpectedStages.Take(failedIndex + 1));
            Assert.Equal(h.ExpectedStages.Take(failedIndex), h.SuccessfulStages);
        }
        finally { h.ReleaseFault(); }
    }

    [Fact, Trait("AC", "AC-97")]
    public async Task ErrorContract_ColorCancellation_DoesNotRequireCallerTokenCancellation()
    {
        using var sdkCancellation = new CancellationTokenSource();
        sdkCancellation.Cancel();
        var original = new OperationCanceledException("SDK-owned cancellation", sdkCancellation.Token);
        using var h = new Harness("global-add", true, false, "global-color-read", "cancel",
            faultFactory: () => original);
        var operation = h.Run();
        var error = await Assert.ThrowsAsync<PpdsException>(() => operation);
        Assert.False(h.Token.IsCancellationRequested);
        Assert.Same(original, error.InnerException);
        Assert.Equal(MetadataErrorCodes.WriteNotConfirmed, error.ErrorCode);
        h.AssertReturned();
    }

    public static IEnumerable<object[]> CodedAcquisitionBoundaries()
    {
        foreach (var (route, stages) in new[]
        {
            ("global-add", new[] { "insert" }),
            ("global-update", new[] { "option-update" }),
            ("status-update", new[] { "option-update", "publish" }),
            ("status-remove", new[] { "option-remove", "publish" }),
            ("table-create", new[] { "solution", "publisher", "table-create", "publish" }),
            ("column-add", new[] { "publish" }), // Colour read-back checkouts: ColorReadBackFailure_AfterTheWrite_IsWriteNotConfirmed.
            ("column-update", new[] { "publish" }) // Shared helper's limited wider reach.
        })
        foreach (var stage in stages)
            yield return [route, stage];
    }

    [Theory, MemberData(nameof(CodedAcquisitionBoundaries)), Trait("AC", "AC-98"), Trait("AC", "AC-101")]
    public async Task ErrorContract_PoolBoundary_IsCodedAndStopsLaterWork(string route, string stage)
    {
        using var h = new Harness(route, route is not ("table-create" or "status-remove"), true,
            stage, "checkout");
        var operation = h.Run();
        var error = await Record.ExceptionAsync(() => operation);
        var coded = Assert.IsAssignableFrom<PpdsException>(error);
        Assert.Equal(ErrorCodes.Connection.Timeout, coded.ErrorCode);
        Assert.True(ReferenceEquals(h.Injected, coded.InnerException) || ReferenceEquals(h.Injected, coded.InnerException?.InnerException));
        Assert.True(operation.IsFaulted);
        AssertCliEnvelope(coded, ErrorCodes.Connection.Timeout, 4);
        var index = Array.IndexOf(h.ExpectedStages, stage);
        h.AssertFinished(h.ExpectedStages.Take(index));
        Assert.Equal(h.ExpectedStages.Take(index), h.SuccessfulStages);
        Assert.All(h.AttemptedStages.GroupBy(x => x), group => Assert.Single(group));
    }

    public static IEnumerable<object[]> AcquisitionIdentityCases()
    {
        foreach (var boundary in CodedAcquisitionBoundaries())
        foreach (var kind in new[] { "cancel", "coded", "validation" })
            yield return [boundary[0], boundary[1], kind];
    }

    [Theory, MemberData(nameof(AcquisitionIdentityCases)), Trait("AC", "AC-98")]
    public async Task ErrorContract_AcquisitionIdentity_PropagatesWithoutWrapping(string route, string stage, string kind)
    {
        Exception original = kind switch
        {
            "cancel" => new OperationCanceledException("SDK-owned acquisition cancellation"),
            "coded" => new PpdsException("Synthetic.Coded", "Already coded",
                new Dictionary<string, object> { ["scope"] = "fixture" }),
            _ => new MetadataValidationException(MetadataErrorCodes.InvalidConstraint, "Already validated")
        };
        using var h = new Harness(route, route is not ("table-create" or "status-remove"), true,
            stage, "checkout", faultFactory: () => original);
        var operation = h.Run();
        var actual = await Record.ExceptionAsync(() => operation);
        if (stage == "publish" && Harness.IsOptionWriteRoute(route))
        {
            // After a committed option write the publish failure is re-raised with what was written, same code/exit.
            Assert.Same(original, actual!.InnerException);
            Assert.Contains("is saved but not published", actual.Message);
            AssertCliEnvelope(actual, ExceptionMapper.Map(original).Code, ExceptionMapper.ToExitCode(original));
            return;
        }
        Assert.Same(original, actual);
        Assert.Equal(kind == "cancel", operation.IsCanceled);
        Assert.Equal(kind != "cancel", operation.IsFaulted);
        Assert.False(h.Token.IsCancellationRequested);
        AssertCliEnvelope(actual!, ExceptionMapper.Map(original).Code, ExceptionMapper.ToExitCode(original));
        h.AssertFinished(h.ExpectedStages.Take(Array.IndexOf(h.ExpectedStages, stage)));
    }

    [Theory, Trait("AC", "AC-99")]
    [InlineData("pool")]
    [InlineData("timeout")]
    [InlineData("configuration")]
    [InlineData("argument")]
    [InlineData("argument-null")]
    [InlineData("credential")]
    [InlineData("credential-custom-code")]
    [InlineData("expired")]
    [InlineData("permission")]
    [InlineData("connection")]
    [InlineData("throttle")]
    [InlineData("file-not-found")]
    [InlineData("directory-not-found")]
    [InlineData("ordinary")]
    public async Task ErrorContract_AcquisitionMapping_PreservesCodeExitAndCause(string kind)
    {
        Exception original = kind switch
        {
            "pool" => new PoolExhaustedException(1, 1, TimeSpan.FromMilliseconds(40)),
            "timeout" => new TimeoutException("Sensitive synthetic detail"),
            "configuration" => new ConfigurationException("fixture", "PoolSize", "Synthetic configuration"),
            "argument" => new ArgumentException("Synthetic argument", "capacity"),
            "argument-null" => new ArgumentNullException("capacity"),
            "credential" => new AuthenticationException("Synthetic credential"),
            "credential-custom-code" => new AuthenticationException("Synthetic credential", "Synthetic.ProviderCode"),
            "expired" => new DataverseAuthenticationException("Synthetic expiry", true),
            "permission" => new DataverseAuthenticationException("Synthetic privilege", false),
            "connection" => new DataverseConnectionException("Synthetic connection"),
            "throttle" => new ServiceProtectionException("Synthetic throttle"),
            "file-not-found" => new FileNotFoundException("Synthetic missing file", "private-fixture-path"),
            "directory-not-found" => new DirectoryNotFoundException("Synthetic missing directory"),
            _ => new InvalidOperationException("Sensitive synthetic detail")
        };
        using var h = new Harness("global-add", false, false, "insert", "checkout",
            faultFactory: () => original);
        var actual = await Record.ExceptionAsync(() => h.Run());
        var coded = Assert.IsAssignableFrom<PpdsException>(actual);
        var (expected, exit) = ExceptionMapper.MapWithExitCode(original);
        Assert.Equal(expected.Code, coded.ErrorCode);
        Assert.Same(original, coded.InnerException);
        AssertCliEnvelope(coded, expected.Code, exit);
        if (exit == ExitCodes.AuthError)
        {
            var auth = Assert.IsType<PpdsAuthException>(coded);
            Assert.Equal(original is DataverseAuthenticationException { RequiresReauthentication: true }, auth.RequiresReauthentication);
        }
        else Assert.Equal("Could not acquire a Dataverse connection.", coded.UserMessage);
        h.AssertFinished([]);
    }
}

public sealed partial class TableUpdateLeaseTests
{
    [Theory, Trait("AC", "AC-101")]
    [InlineData("update-checkout", "pool")]
    [InlineData("publish-checkout", "pool")]
    [InlineData("update-checkout", "timeout")]
    [InlineData("publish-checkout", "timeout")]
    [InlineData("update-checkout", "ordinary")]
    [InlineData("publish-checkout", "ordinary")]
    public async Task ErrorContract_Acquisition_PreservesMappingCauseAndPartialProgress(string boundary, string kind)
    {
        using var h = new Harness(true, failureBoundary: boundary, failureKind: kind);
        var actual = await Record.ExceptionAsync(h.Invoke);
        var coded = Assert.IsAssignableFrom<PpdsException>(actual);
        Assert.Same(h.Injected, coded.InnerException);
        AssertCli(coded, ExceptionMapper.Map(h.Injected!).Code, ExceptionMapper.ToExitCode(h.Injected!));
        // AC-147: the table's labels are read before the update.
        Assert.Equal(boundary == "publish-checkout" ? new[] { "read", "update", "confirm" } : ["read"], h.SuccessfulSdk);
        Assert.Equal(boundary == "publish-checkout" ? "Changed" : "Existing", h.StoredLabel);
        Assert.Empty(h.Infos);
        h.AssertReturned();
    }
}

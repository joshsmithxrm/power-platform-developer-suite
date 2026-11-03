using System.Reflection;
using System.Text.Json;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public class PluginPublicContractCompatibilityTests
{
    private static readonly Type[] OriginalStepParameters =
        [typeof(string), typeof(string), typeof(int?), typeof(string), typeof(string),
         typeof(bool?), typeof(bool?), typeof(string)];
    private static readonly object?[] OriginalStepValues =
        ["Asynchronous", "PostOperation", 7, "name", "description", true, false, "Child"];

    [Fact]
    public void StepUpdateRequest_OriginalEightParameterConstructorRemainsCallable()
    {
        var ctor = typeof(StepUpdateRequest).GetConstructor(OriginalStepParameters);
        Assert.NotNull(ctor);
        var request = Assert.IsType<StepUpdateRequest>(ctor.Invoke(OriginalStepValues));
        Assert.Equal("Asynchronous", request.Mode);
        Assert.Equal("PostOperation", request.Stage);
        Assert.Equal(7, request.Rank);
        Assert.Equal("name", request.FilteringAttributes);
        Assert.Equal("description", request.Description);
        Assert.True(request.CanBeBypassed);
        Assert.False(request.CanUseReadOnlyConnection);
        Assert.Equal("Child", request.InvocationSource);
    }

    [Fact]
    public void StepUpdateRequest_OriginalEightParameterDeconstructRemainsCallable()
    {
        var method = typeof(StepUpdateRequest).GetMethod("Deconstruct",
            OriginalStepParameters.Select(t => t.MakeByRefType()).ToArray());
        Assert.NotNull(method);
        var output = new object?[8];
        method.Invoke(new StepUpdateRequest("Asynchronous", "PostOperation", 7,
            "name", "description", true, false, "Child"), output);
        Assert.Equal(OriginalStepValues, output);
    }

    [Fact]
    public void ImageUpdateRequest_OriginalTwoParameterConstructorRemainsCallable()
    {
        var ctor = typeof(ImageUpdateRequest).GetConstructor([typeof(string), typeof(string)]);
        Assert.NotNull(ctor);
        var request = Assert.IsType<ImageUpdateRequest>(ctor.Invoke(["name,address1_city", "before"]));
        Assert.Equal("name,address1_city", request.Attributes);
        Assert.Equal("before", request.Name);
    }

    [Fact]
    public void ImageUpdateRequest_OriginalTwoParameterDeconstructRemainsCallable()
    {
        var method = typeof(ImageUpdateRequest).GetMethod("Deconstruct",
            [typeof(string).MakeByRefType(), typeof(string).MakeByRefType()]);
        Assert.NotNull(method);
        var output = new object?[2];
        method.Invoke(new ImageUpdateRequest("name", "before"), output);
        Assert.Equal(new object?[] { "name", "before" }, output);
    }

    [Fact]
    public void NewResolver_DoesNotAddAnAbstractObligationToExistingImplementors()
    {
        var resolver = typeof(IPluginRegistrationService).GetMethod("ResolveRunAsUserAsync",
            [typeof(string), typeof(CancellationToken)]);
        Assert.NotNull(resolver);
        Assert.False(resolver.IsAbstract);
        Assert.NotNull(resolver.GetMethodBody());
    }

    [Fact]
    public void GenericStepJsonRoundTrip_PreservesPatchIntentAndSuppliedValues()
    {
        var original = new PluginStepConfig
        {
            Name = "step", Message = "Update", Entity = "account", Stage = "PostOperation",
            Description = "", FilteringAttributes = "name"
        };
        Assert.Equal(RegistrationOperationIntent.Patch, original.OperationIntent);
        var roundTrip = JsonSerializer.Deserialize<PluginStepConfig>(JsonSerializer.Serialize(original))!;
        Assert.Equal(RegistrationOperationIntent.Patch, roundTrip.OperationIntent);
        Assert.Equal("", roundTrip.Description);
        Assert.Equal("name", roundTrip.FilteringAttributes);
        Assert.True(roundTrip.IsSpecified("description"));
        // The original generic serializer emits null optional properties. Their
        // presence after reading must not turn a Patch null into a reset/write.
        Assert.Null(roundTrip.UnsecureConfiguration);
        var patch = PluginRegistrationNormalizer.NormalizeStep(roundTrip, roundTrip.OperationIntent);
        Assert.False(patch.ContainsKey("unsecureConfiguration"));
    }

    [Fact]
    public void GenericImageJsonRoundTrip_PreservesPatchIntentAndSuppliedValues()
    {
        var original = new PluginImageConfig { Name = "before", Attributes = "" };
        Assert.Equal(RegistrationOperationIntent.Patch, original.OperationIntent);
        var roundTrip = JsonSerializer.Deserialize<PluginImageConfig>(JsonSerializer.Serialize(original))!;
        Assert.Equal(RegistrationOperationIntent.Patch, roundTrip.OperationIntent);
        Assert.Equal("", roundTrip.Attributes);
        Assert.True(roundTrip.IsSpecified("attributes"));
        Assert.False(roundTrip.IsSpecified("description"));
    }

    [Fact]
    public void GenericStepJsonRoundTrip_DoesNotDiscardSuppliedSecureInput()
    {
        // Synthetic test data only; this is generic model persistence, not public output.
        const string synthetic = "synthetic-input-roundtrip";
        var input = JsonSerializer.Deserialize<PluginStepConfig>(
            """{"name":"step","secureConfiguration":"synthetic-input-roundtrip"}""")!;
        Assert.Equal(synthetic, input.SecureConfiguration);
        var roundTrip = JsonSerializer.Deserialize<PluginStepConfig>(JsonSerializer.Serialize(input))!;
        Assert.Equal(synthetic, roundTrip.SecureConfiguration);
    }
}

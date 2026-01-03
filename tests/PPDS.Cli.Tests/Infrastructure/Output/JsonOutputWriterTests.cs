using System.Text.Json;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Output;

public class JsonOutputWriterTests
{
    [Fact]
    public void IsJsonMode_ReturnsTrue()
    {
        var writer = new JsonOutputWriter();
        Assert.True(writer.IsJsonMode);
    }

    [Fact]
    public void WriteSuccess_OutputsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        writer.WriteSuccess(new { name = "test" });

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("success", out var success));
        Assert.True(success.GetBoolean());
    }

    [Fact]
    public void WriteSuccess_IncludesVersionField()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        writer.WriteSuccess("test");

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("version", out var version));
        Assert.Equal(JsonOutputWriter.SchemaVersion, version.GetString());
    }

    [Fact]
    public void WriteSuccess_IncludesTimestamp()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        writer.WriteSuccess("test");

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("timestamp", out var timestamp));
        Assert.NotNull(timestamp.GetString());
        Assert.True(DateTime.TryParse(timestamp.GetString(), out _));
    }

    [Fact]
    public void WriteSuccess_IncludesData()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        writer.WriteSuccess(new { value = 42 });

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("data", out var data));
        Assert.True(data.TryGetProperty("value", out var value));
        Assert.Equal(42, value.GetInt32());
    }

    [Fact]
    public void WriteError_OutputsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        var error = new StructuredError(ErrorCodes.Auth.ProfileNotFound, "Profile not found", null, "myprofile");
        writer.WriteError(error);

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("success", out var success));
        Assert.False(success.GetBoolean());
    }

    [Fact]
    public void WriteError_IncludesErrorDetails()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        var error = new StructuredError(ErrorCodes.Auth.ProfileNotFound, "Profile not found", null, "myprofile");
        writer.WriteError(error);

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out var errorObj));
        Assert.True(errorObj.TryGetProperty("code", out var code));
        Assert.Equal(ErrorCodes.Auth.ProfileNotFound, code.GetString());
        Assert.True(errorObj.TryGetProperty("message", out var message));
        Assert.Equal("Profile not found", message.GetString());
        Assert.True(errorObj.TryGetProperty("target", out var target));
        Assert.Equal("myprofile", target.GetString());
    }

    [Fact]
    public void WritePartialSuccess_IncludesResults()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        var results = new List<ItemResult>
        {
            ItemResult.Ok("item1", "success"),
            ItemResult.Fail("item2", new StructuredError(ErrorCodes.Operation.NotFound, "Not found"))
        };

        writer.WritePartialSuccess("some data", results);

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("results", out var resultsArray));
        Assert.Equal(2, resultsArray.GetArrayLength());
    }

    [Fact]
    public void WriteResult_WithSuccess_SetsSuccessTrue()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        var result = CommandResult<string>.Ok("test data");
        writer.WriteResult(result);

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("success", out var success));
        Assert.True(success.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("data", out var data));
        Assert.Equal("test data", data.GetString());
    }

    [Fact]
    public void WriteResult_WithError_SetsSuccessFalse()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        var error = new StructuredError(ErrorCodes.Operation.Internal, "Internal error");
        var result = CommandResult<string>.Fail(error);
        writer.WriteResult(result);

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("success", out var success));
        Assert.False(success.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void WriteMessage_OutputsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        writer.WriteMessage("Hello world");

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("success", out var success));
        Assert.True(success.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("data", out var data));
        Assert.True(data.TryGetProperty("message", out var message));
        Assert.Equal("Hello world", message.GetString());
    }

    [Fact]
    public void WriteWarning_OutputsValidJson()
    {
        using var sw = new StringWriter();
        var writer = new JsonOutputWriter(sw);

        writer.WriteWarning("This is a warning");

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("success", out var success));
        Assert.True(success.GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("data", out var data));
        Assert.True(data.TryGetProperty("warning", out var warning));
        Assert.Equal("This is a warning", warning.GetString());
    }

    [Fact]
    public void DebugMode_IsFalseByDefault()
    {
        var writer = new JsonOutputWriter();
        Assert.False(writer.DebugMode);
    }

    [Fact]
    public void DebugMode_CanBeSetToTrue()
    {
        var writer = new JsonOutputWriter(debugMode: true);
        Assert.True(writer.DebugMode);
    }
}

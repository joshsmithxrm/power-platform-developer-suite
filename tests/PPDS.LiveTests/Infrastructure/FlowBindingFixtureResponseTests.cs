using System.Text.Json;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Offline response checks only; no credentials, processes or Dataverse calls.</summary>
public sealed class FlowBindingFixtureResponseTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"connectionId\":null}")]
    [InlineData("{\"connectionId\":\"\"}")]
    public void RequireUnboundConnectionId_AcceptsUnboundRepresentations(string json)
    {
        var response = FlowBindingFixtureResponse.Parse(json);
        FlowBindingFixtureResponse.RequireUnboundConnectionId(response);
    }

    [Theory]
    [InlineData("{\"connectionId\":\"connection-1\"}")]
    [InlineData("{\"connectionId\":\" \"}")]
    [InlineData("{\"connectionId\":0}")]
    [InlineData("{\"connectionId\":false}")]
    [InlineData("{\"connectionId\":[]}")]
    [InlineData("{\"connectionId\":{}}")]
    public void RequireUnboundConnectionId_RejectsBindingsAndWrongTypes(string json)
    {
        var response = FlowBindingFixtureResponse.Parse(json);
        Assert.Throws<InvalidOperationException>(() => FlowBindingFixtureResponse.RequireUnboundConnectionId(response));
    }

    [Fact]
    public void Parse_ReturnsResponseUsableAfterDocumentDisposal()
    {
        var response = FlowBindingFixtureResponse.Parse("{\"data\":{\"connectionId\":\"\"},\"success\":false}");
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal(string.Empty, response.GetProperty("data").GetProperty("connectionId").GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public void Parse_RejectsNonObjectResponse(string json)
        => Assert.Throws<InvalidOperationException>(() => FlowBindingFixtureResponse.Parse(json));

    [Fact]
    public void Parse_RejectsMalformedResponse()
        => Assert.ThrowsAny<JsonException>(() => FlowBindingFixtureResponse.Parse("{"));
}

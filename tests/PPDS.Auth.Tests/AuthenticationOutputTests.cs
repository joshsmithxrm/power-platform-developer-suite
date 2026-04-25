using FluentAssertions;
using PPDS.Auth;
using Xunit;

namespace PPDS.Auth.Tests;

[Collection("AuthenticationOutput")]
public class AuthenticationOutputTests
{
    [Fact]
    public void Writer_DefaultsToConsoleWriteLine()
    {
        AuthenticationOutput.Reset();

        var writer = AuthenticationOutput.Writer;

        writer.Should().NotBeNull();
    }

    [Fact]
    public void Writer_CanBeSetToNull()
    {
        AuthenticationOutput.Writer = null;

        AuthenticationOutput.Writer.Should().BeNull();
    }

    [Fact]
    public void Writer_CanBeSetToCustomAction()
    {
        var captured = "";
        AuthenticationOutput.Writer = msg => captured = msg;

        AuthenticationOutput.Writer.Should().NotBeNull();
    }

    [Fact]
    public void Reset_RestoresDefaultWriter()
    {
        AuthenticationOutput.Writer = null;

        AuthenticationOutput.Reset();

        AuthenticationOutput.Writer.Should().NotBeNull();
    }

    [Fact]
    public void WriteLine_WithNullWriter_DoesNotThrow()
    {
        AuthenticationOutput.Writer = null;

        var act = () => typeof(AuthenticationOutput)
            .GetMethod("WriteLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { "test" });

        act.Should().NotThrow();
    }

    [Fact]
    public void WriteLine_WithCustomWriter_CallsWriter()
    {
        var captured = "";
        AuthenticationOutput.Writer = msg => captured = msg;

        typeof(AuthenticationOutput)
            .GetMethod("WriteLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { "test message" });

        captured.Should().Be("test message");
    }
}

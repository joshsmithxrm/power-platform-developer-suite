using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Output;

public class TextOutputWriterTests
{
    [Fact]
    public void IsJsonMode_ReturnsFalse()
    {
        var writer = new TextOutputWriter();
        Assert.False(writer.IsJsonMode);
    }

    [Fact]
    public void DebugMode_IsFalseByDefault()
    {
        var writer = new TextOutputWriter();
        Assert.False(writer.DebugMode);
    }

    [Fact]
    public void DebugMode_CanBeSetToTrue()
    {
        var writer = new TextOutputWriter(debugMode: true);
        Assert.True(writer.DebugMode);
    }

    [Fact]
    public void WriteError_WritesMessage()
    {
        // Capture stderr
        var oldError = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            var writer = new TextOutputWriter();
            var error = new StructuredError(ErrorCodes.Auth.ProfileNotFound, "Profile not found");
            writer.WriteError(error);

            var output = sw.ToString();
            Assert.Contains("Error:", output);
            Assert.Contains("Profile not found", output);
        }
        finally
        {
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void WriteError_WritesTarget_WhenProvided()
    {
        var oldError = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            var writer = new TextOutputWriter();
            var error = new StructuredError(ErrorCodes.Validation.FileNotFound, "File not found", null, "schema.xml");
            writer.WriteError(error);

            var output = sw.ToString();
            Assert.Contains("Target:", output);
            Assert.Contains("schema.xml", output);
        }
        finally
        {
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void WriteError_WritesDetails_WhenProvided()
    {
        var oldError = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            var writer = new TextOutputWriter();
            var error = new StructuredError(ErrorCodes.Operation.Internal, "Error", "Additional context");
            writer.WriteError(error);

            var output = sw.ToString();
            Assert.Contains("Details:", output);
            Assert.Contains("Additional context", output);
        }
        finally
        {
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void WriteError_ShowsCode_InDebugMode()
    {
        var oldError = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            var writer = new TextOutputWriter(debugMode: true);
            var error = new StructuredError(ErrorCodes.Auth.ProfileNotFound, "Profile not found");
            writer.WriteError(error);

            var output = sw.ToString();
            Assert.Contains("Code:", output);
            Assert.Contains(ErrorCodes.Auth.ProfileNotFound, output);
        }
        finally
        {
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void WriteError_HidesCode_WhenNotDebugMode()
    {
        var oldError = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);

        try
        {
            var writer = new TextOutputWriter(debugMode: false);
            var error = new StructuredError(ErrorCodes.Auth.ProfileNotFound, "Profile not found");
            writer.WriteError(error);

            var output = sw.ToString();
            Assert.DoesNotContain("Code:", output);
        }
        finally
        {
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void WriteSuccess_WritesStringData()
    {
        var oldOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            var writer = new TextOutputWriter();
            writer.WriteSuccess("Hello world");

            var output = sw.ToString();
            Assert.Contains("Hello world", output);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }

    [Fact]
    public void WriteMessage_WritesToStdout()
    {
        var oldOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            var writer = new TextOutputWriter();
            writer.WriteMessage("Status update");

            var output = sw.ToString();
            Assert.Contains("Status update", output);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }

    [Fact]
    public void WriteWarning_IncludesWarningPrefix()
    {
        var oldOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            var writer = new TextOutputWriter();
            writer.WriteWarning("This might be a problem");

            var output = sw.ToString();
            Assert.Contains("Warning:", output);
            Assert.Contains("This might be a problem", output);
        }
        finally
        {
            Console.SetOut(oldOut);
        }
    }
}

using FluentAssertions;
using PPDS.Analyzers.Rules;
using Xunit;

namespace PPDS.Analyzers.Tests.Rules;

[Trait("Category", "Unit")]
public class UseStructuredExceptionsAnalyzerTests
{
    /// <summary>AC-01: Flags throw new Exception("msg") in Services/ path.</summary>
    [Fact]
    public async Task FlagsRawExceptionInServices()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork() => throw new Exception("something failed");
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS004");
    }

    /// <summary>AC-02: Flags throw new InvalidOperationException in Services/.</summary>
    [Fact]
    public async Task FlagsInvalidOperationExceptionInServices()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork() { throw new InvalidOperationException("not valid"); }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS004");
    }

    /// <summary>AC-03: Flags throw new ArgumentException in Services/.</summary>
    [Fact]
    public async Task FlagsArgumentExceptionInServices()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork(string x)
                {
                    throw new ArgumentException("bad arg", nameof(x));
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().ContainSingle()
            .Which.Id.Should().Be("PPDS004");
    }

    /// <summary>AC-04: Does NOT flag throw new PpdsException(...).</summary>
    [Fact]
    public async Task DoesNotFlagPpdsException()
    {
        const string code = """
            using System;
            class PpdsException : Exception
            {
                public string ErrorCode { get; }
                public PpdsException(string errorCode, string message) : base(message)
                {
                    ErrorCode = errorCode;
                }
            }
            class MyService
            {
                void DoWork() => throw new PpdsException("Profile.NotFound", "not found");
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-04 (subclass): Does NOT flag PpdsException subclass.</summary>
    [Fact]
    public async Task DoesNotFlagPpdsExceptionSubclass()
    {
        const string code = """
            using System;
            class PpdsException : Exception
            {
                public PpdsException(string code, string msg) : base(msg) { }
            }
            class PpdsValidationException : PpdsException
            {
                public PpdsValidationException(string msg) : base("Validation", msg) { }
            }
            class MyService
            {
                void DoWork() => throw new PpdsValidationException("bad input");
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-05: Does NOT flag throws outside Services/.</summary>
    [Fact]
    public async Task DoesNotFlagOutsideServices()
    {
        const string code = """
            using System;
            class MyCommand
            {
                void Execute() => throw new InvalidOperationException("oops");
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Commands/MyCommand.cs");

        diagnostics.Should().BeEmpty();
    }

    /// <summary>AC-06: Does NOT flag re-throws (throw;).</summary>
    [Fact]
    public async Task DoesNotFlagRethrow()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork()
                {
                    try { }
                    catch (Exception) { throw; }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotFlagRethrowVariable()
    {
        const string code = """
            using System;
            class MyService
            {
                void DoWork()
                {
                    try { }
                    catch (Exception ex) { throw ex; }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHelper
            .GetDiagnosticsAsync<UseStructuredExceptionsAnalyzer>(code, "/src/PPDS.Cli/Services/MyService.cs");

        // throw ex; is IdentifierNameSyntax, not ObjectCreationExpression — not flagged
        diagnostics.Should().BeEmpty();
    }
}

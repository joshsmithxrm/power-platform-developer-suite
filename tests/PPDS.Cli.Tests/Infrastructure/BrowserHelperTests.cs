using System;
using FluentAssertions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="BrowserHelper"/> URL scheme validation (A4).
/// </summary>
/// <remarks>
/// These tests only exercise <see cref="BrowserHelper.ValidateUrl"/> via
/// the public <see cref="BrowserHelper.OpenUrl"/> entry point. They never
/// actually launch a browser — <see cref="BrowserHelper.OpenUrl"/> throws
/// before reaching <c>Process.Start</c> for every non-http(s) scheme.
/// </remarks>
public class BrowserHelperTests
{
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ftp://example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("ms-excel:ofe|u|https://example.com/foo.xlsx")]
    [InlineData("mailto:user@example.com")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ssh://user@host")]
    public void OpenUrl_RejectsNonHttpSchemes(string url)
    {
        var act = () => BrowserHelper.OpenUrl(url);

        act.Should().Throw<PpdsException>()
            .Where(e => e.ErrorCode == ErrorCodes.Validation.InvalidUrlScheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OpenUrl_RejectsEmptyUrls(string? url)
    {
        var act = () => BrowserHelper.OpenUrl(url!);

        act.Should().Throw<PpdsException>()
            .Where(e => e.ErrorCode == ErrorCodes.Validation.InvalidUrlScheme);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("example.com")] // Not absolute
    [InlineData("//example.com/path")] // Protocol-relative
    public void OpenUrl_RejectsNonAbsoluteUrls(string url)
    {
        var act = () => BrowserHelper.OpenUrl(url);

        act.Should().Throw<PpdsException>()
            .Where(e => e.ErrorCode == ErrorCodes.Validation.InvalidUrlScheme);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://login.microsoftonline.com/common")]
    [InlineData("https://dataverse.example.com/main.aspx?pagetype=entityrecord&id=abc")]
    public void OpenUrl_AcceptsHttpAndHttpsSchemes(string url)
    {
        // We can't assert on Process.Start side-effects portably, so we
        // only assert that validation does *not* throw. OpenUrl will still
        // return true/false based on whether the launch succeeds, which is
        // outside the scope of this test.
        Action act = () =>
        {
            try
            {
                BrowserHelper.OpenUrl(url);
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Validation.InvalidUrlScheme)
            {
                throw; // surface validation errors
            }
            catch
            {
                // Any other error (no browser, no display, etc.) is fine for this test.
            }
        };

        act.Should().NotThrow<PpdsException>();
    }
}

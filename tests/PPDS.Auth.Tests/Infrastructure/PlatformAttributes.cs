using System.Runtime.InteropServices;
using Xunit;

namespace PPDS.Auth.Tests.Infrastructure;

/// <summary>
/// Fact that is skipped (not silently passed) when not running on Windows.
/// Use for tests that exercise APIs which only exist on Windows (X509Store, DPAPI, etc.).
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test requires Windows.";
        }
    }
}

/// <summary>
/// Theory that is skipped (not silently passed) when not running on Windows.
/// </summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test requires Windows.";
        }
    }
}

/// <summary>
/// Fact that is skipped on Windows. Use for tests that assert non-Windows behavior
/// (e.g., PlatformNotSupportedException for Windows-only credential stores).
/// </summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "This test requires a non-Windows platform.";
        }
    }
}

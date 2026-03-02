using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

[Trait("Category", "TuiUnit")]
public class PpdsClipboardTests
{
    // All tests simulate Linux (IsWindows=false, IsMacOS=false) unless stated otherwise.
    // This is the interesting platform since it has the most fallback logic.

    // ── IsSupported ──────────────────────────────────────────────────

    [Fact]
    public void IsSupported_AlwaysReturnsTrue()
    {
        var clipboard = new PpdsClipboard(new FakeClipboardBackend());

        Assert.True(clipboard.IsSupported);
    }

    // ── Copy (TrySetClipboardData) ───────────────────────────────────

    [Fact]
    public void TrySetClipboardData_UsesPlatformTool_WhenAvailable()
    {
        var backend = new FakeClipboardBackend
        {
            HasDisplayServer = true,
            PlatformCopySucceeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        var result = clipboard.TrySetClipboardData("hello");

        Assert.True(result);
        Assert.Equal("hello", backend.LastPlatformCopyText);
        Assert.Null(backend.LastOsc52Text);
    }

    [Fact]
    public void TrySetClipboardData_FallsBackToOsc52_WhenPlatformFails()
    {
        var backend = new FakeClipboardBackend
        {
            HasDisplayServer = true,
            PlatformCopySucceeds = false,
            Osc52Succeeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        var result = clipboard.TrySetClipboardData("hello");

        Assert.True(result);
        Assert.Equal("hello", backend.LastOsc52Text);
    }

    [Fact]
    public void TrySetClipboardData_ReturnsFalse_WhenAllMethodsFail()
    {
        var backend = new FakeClipboardBackend
        {
            PlatformCopySucceeds = false,
            Osc52Succeeds = false
        };
        var clipboard = new PpdsClipboard(backend);

        var result = clipboard.TrySetClipboardData("hello");

        Assert.False(result);
    }

    // ── Paste (TryGetClipboardData) ──────────────────────────────────

    [Fact]
    public void TryGetClipboardData_ReturnsPlatformContent_WhenAvailable()
    {
        var backend = new FakeClipboardBackend
        {
            HasDisplayServer = true,
            PlatformPasteContent = "pasted text"
        };
        var clipboard = new PpdsClipboard(backend);

        var result = clipboard.TryGetClipboardData(out var text);

        Assert.True(result);
        Assert.Equal("pasted text", text);
    }

    [Fact]
    public void TryGetClipboardData_ReturnsFalse_WhenNoPlatformAvailable()
    {
        var backend = new FakeClipboardBackend { PlatformPasteContent = null };
        var clipboard = new PpdsClipboard(backend);

        var result = clipboard.TryGetClipboardData(out _);

        Assert.False(result);
    }

    // ── Platform strategy ordering ───────────────────────────────────

    [Fact]
    public void TrySetClipboardData_OnWsl_TriesClipExe()
    {
        var backend = new FakeClipboardBackend
        {
            IsWsl = true,
            HasDisplayServer = false,
            PlatformCopySucceeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        clipboard.TrySetClipboardData("test");

        Assert.Contains("clip.exe", backend.AttemptedPrograms);
    }

    [Fact]
    public void TrySetClipboardData_WithDisplay_TriesXclipThenXsel()
    {
        var backend = new FakeClipboardBackend
        {
            IsWsl = false,
            HasDisplayServer = true,
            PlatformCopySucceeds = false,
            Osc52Succeeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        clipboard.TrySetClipboardData("test");

        Assert.Contains("xclip", backend.AttemptedPrograms);
        Assert.Contains("xsel", backend.AttemptedPrograms);
    }

    [Fact]
    public void TrySetClipboardData_NoWslNoDisplay_SkipsPlatformTools_GoesDirectToOsc52()
    {
        var backend = new FakeClipboardBackend
        {
            IsWsl = false,
            HasDisplayServer = false,
            Osc52Succeeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        clipboard.TrySetClipboardData("test");

        Assert.Empty(backend.AttemptedPrograms);
        Assert.Equal("test", backend.LastOsc52Text);
    }

    [Fact]
    public void TrySetClipboardData_OnWindows_TriesClip()
    {
        var backend = new FakeClipboardBackend
        {
            IsWindows = true,
            PlatformCopySucceeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        clipboard.TrySetClipboardData("test");

        Assert.Contains("clip", backend.AttemptedPrograms);
    }

    [Fact]
    public void TrySetClipboardData_OnMacOS_TriesPbcopy()
    {
        var backend = new FakeClipboardBackend
        {
            IsMacOS = true,
            PlatformCopySucceeds = true
        };
        var clipboard = new PpdsClipboard(backend);

        clipboard.TrySetClipboardData("test");

        Assert.Contains("pbcopy", backend.AttemptedPrograms);
    }

    // ── Fake backend ────────────────────────────────────────────────

    internal class FakeClipboardBackend : IClipboardBackend
    {
        public bool PlatformCopySucceeds { get; set; }
        public bool Osc52Succeeds { get; set; }
        public string? PlatformPasteContent { get; set; }
        public bool IsWindows { get; set; }
        public bool IsMacOS { get; set; }
        public bool IsWsl { get; set; }
        public bool HasDisplayServer { get; set; }

        public string? LastPlatformCopyText { get; private set; }
        public string? LastOsc52Text { get; private set; }
        public List<string> AttemptedPrograms { get; } = new();

        public bool CopyViaProcess(string program, string args, string text)
        {
            AttemptedPrograms.Add(program);
            if (PlatformCopySucceeds)
            {
                LastPlatformCopyText = text;
                return true;
            }
            return false;
        }

        public string? PasteViaProcess(string program, string args)
        {
            AttemptedPrograms.Add(program);
            return PlatformPasteContent;
        }

        public bool CopyViaOsc52(string text)
        {
            LastOsc52Text = text;
            return Osc52Succeeds;
        }
    }
}

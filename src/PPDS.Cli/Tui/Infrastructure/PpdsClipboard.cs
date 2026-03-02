using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Clipboard implementation that works on Linux, SSH, WSL, and macOS.
/// Tries platform-native tools first, falls back to OSC 52 terminal escape.
/// Replaces Terminal.Gui's built-in clipboard which fails without xclip on Linux.
/// </summary>
internal sealed class PpdsClipboard : ClipboardBase
{
    private readonly IClipboardBackend _backend;

    public PpdsClipboard() : this(new SystemClipboardBackend()) { }

    public PpdsClipboard(IClipboardBackend backend)
    {
        _backend = backend;
    }

    public override bool IsSupported => true;

    protected override string GetClipboardDataImpl()
    {
        if (_backend.IsWindows)
        {
            var result = _backend.PasteViaProcess("powershell", "-noprofile -command Get-Clipboard");
            if (result != null) return result;
        }
        else if (_backend.IsMacOS)
        {
            var result = _backend.PasteViaProcess("pbpaste", string.Empty);
            if (result != null) return result;
        }
        else
        {
            if (_backend.IsWsl)
            {
                var result = _backend.PasteViaProcess("powershell.exe", "-noprofile -command Get-Clipboard");
                if (result != null) return result;
            }

            if (_backend.HasDisplayServer)
            {
                var result = _backend.PasteViaProcess("xclip", "-selection clipboard -o");
                if (result != null) return result;

                result = _backend.PasteViaProcess("xsel", "--clipboard --output");
                if (result != null) return result;
            }
        }

        // No platform paste available (OSC 52 can't read clipboard synchronously).
        // Throw so ClipboardBase.TryGetClipboardData returns false.
        throw new NotSupportedException();
    }

    protected override void SetClipboardDataImpl(string text)
    {
        if (TryPlatformCopy(text))
            return;

        if (_backend.CopyViaOsc52(text))
            return;

        // Nothing worked. Throw so ClipboardBase.TrySetClipboardData returns false.
        throw new NotSupportedException();
    }

    private bool TryPlatformCopy(string text)
    {
        if (_backend.IsWindows)
            return _backend.CopyViaProcess("clip", string.Empty, text);

        if (_backend.IsMacOS)
            return _backend.CopyViaProcess("pbcopy", string.Empty, text);

        // Linux
        if (_backend.IsWsl && _backend.CopyViaProcess("clip.exe", string.Empty, text))
            return true;

        if (_backend.HasDisplayServer)
        {
            if (_backend.CopyViaProcess("xclip", "-selection clipboard", text))
                return true;

            if (_backend.CopyViaProcess("xsel", "--clipboard --input", text))
                return true;
        }

        return false;
    }
}

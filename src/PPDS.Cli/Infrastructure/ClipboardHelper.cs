using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Cross-platform helper for clipboard operations.
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// Copies text to the system clipboard.
    /// </summary>
    /// <param name="text">The text to copy.</param>
    /// <returns>True if the text was copied successfully, false otherwise.</returns>
    public static bool CopyToClipboard(string text)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Pipe directly to clip.exe via stdin (avoids escaping issues)
                return CopyWithProcess("clip", string.Empty, text);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return CopyWithProcess("pbcopy", string.Empty, text);
            }
            else
            {
                // Linux - try xclip first, then xsel
                if (CopyWithProcess("xclip", "-selection clipboard", text))
                {
                    return true;
                }
                return CopyWithProcess("xsel", "--clipboard --input", text);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not copy to clipboard: {ex.Message}");
            return false;
        }
    }

    private static bool CopyWithProcess(string fileName, string arguments, string? standardInput = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = standardInput != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            if (standardInput != null)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

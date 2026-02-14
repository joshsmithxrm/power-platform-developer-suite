using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Real clipboard backend that spawns processes and writes OSC 52 to /dev/tty.
/// </summary>
internal sealed class SystemClipboardBackend : IClipboardBackend
{
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public bool IsWsl => Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null;

    public bool HasDisplayServer =>
        Environment.GetEnvironmentVariable("DISPLAY") != null
        || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null;

    public bool CopyViaProcess(string program, string args, string text)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = program,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public string? PasteViaProcess(string program, string args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = program,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    public bool CopyViaOsc52(string text)
    {
        try
        {
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            var osc52 = $"\x1b]52;c;{base64}\x07";

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists("/dev/tty"))
            {
                using var tty = new FileStream("/dev/tty", FileMode.Open, FileAccess.Write);
                var bytes = Encoding.UTF8.GetBytes(osc52);
                tty.Write(bytes, 0, bytes.Length);
                tty.Flush();
                return true;
            }

            if (!Console.IsOutputRedirected)
            {
                Console.Write(osc52);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

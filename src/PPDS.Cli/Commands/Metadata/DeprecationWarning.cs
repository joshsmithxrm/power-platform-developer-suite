namespace PPDS.Cli.Commands.Metadata;

internal static class DeprecationWarning
{
    internal static void Write(string deprecatedCmd, string canonicalCmd)
    {
        Console.Error.WriteLine($"warning: '{deprecatedCmd}' is deprecated and will be removed in a future release.");
        Console.Error.WriteLine($"         Use '{canonicalCmd}' instead.");
    }
}

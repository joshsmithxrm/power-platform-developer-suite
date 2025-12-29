namespace PPDS.Cli.Commands;

/// <summary>
/// Standard exit codes for the CLI tool.
/// </summary>
public static class ExitCodes
{
    /// <summary>Operation completed successfully.</summary>
    public const int Success = 0;

    /// <summary>Partial success - some records failed but operation completed.</summary>
    public const int PartialSuccess = 1;

    /// <summary>Operation failed - could not complete.</summary>
    public const int Failure = 2;

    /// <summary>Invalid arguments provided.</summary>
    public const int InvalidArguments = 3;
}

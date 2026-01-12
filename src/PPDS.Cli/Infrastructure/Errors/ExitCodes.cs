namespace PPDS.Cli.Infrastructure.Errors;

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

    /// <summary>Connection to Dataverse failed.</summary>
    public const int ConnectionError = 4;

    /// <summary>Authentication failed.</summary>
    public const int AuthError = 5;

    /// <summary>Resource not found (profile, environment, file).</summary>
    public const int NotFoundError = 6;

    /// <summary>Mapping required - auto-mapping incomplete, need --generate-mapping or --force.</summary>
    public const int MappingRequired = 7;

    /// <summary>Validation error - incomplete mapping file, schema mismatch, etc.</summary>
    public const int ValidationError = 8;

    /// <summary>Forbidden - action not allowed (e.g., deleting managed component).</summary>
    public const int Forbidden = 9;

    /// <summary>Precondition failed - operation blocked by current state (e.g., has children).</summary>
    public const int PreconditionFailed = 10;
}

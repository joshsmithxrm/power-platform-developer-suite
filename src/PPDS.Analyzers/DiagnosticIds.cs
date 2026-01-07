namespace PPDS.Analyzers;

/// <summary>
/// Diagnostic IDs for PPDS analyzers.
/// See .github/CODE_SCANNING.md for rationale on each rule.
/// </summary>
public static class DiagnosticIds
{
    // Architectural rules (from ADRs 0015/0024/0025/0026)
    public const string NoDirectFileIoInUi = "PPDS001";
    public const string NoConsoleInServices = "PPDS002";
    public const string NoUiFrameworkInServices = "PPDS003";
    public const string UseStructuredExceptions = "PPDS004";
    public const string NoSdkInPresentation = "PPDS005";
    public const string UseEarlyBoundEntities = "PPDS006";
    public const string PoolClientInParallel = "PPDS007";

    // Performance/Correctness rules (from PR bot feedback)
    public const string UseBulkOperations = "PPDS008";
    public const string UseAggregateForCount = "PPDS009";
    public const string ValidateTopCount = "PPDS010";
    public const string PropagateCancellation = "PPDS011";
    public const string NoSyncOverAsync = "PPDS012";
    public const string NoFireAndForgetInCtor = "PPDS013";
}

/// <summary>
/// Diagnostic categories for PPDS analyzers.
/// </summary>
public static class DiagnosticCategories
{
    public const string Architecture = "PPDS.Architecture";
    public const string Performance = "PPDS.Performance";
    public const string Correctness = "PPDS.Correctness";
    public const string Style = "PPDS.Style";
}

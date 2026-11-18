using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Query;

/// <summary>
/// The DML live differential run: skipped (with the reason) unless the metadata live prerequisites are set and the
/// operator names the organisation in PPDS_DML_LIVE_CONFIRM_URL; skipped too when only the cleanup operation is asked for.
/// </summary>
public sealed class DmlLiveFactAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    public DmlLiveFactAttribute()
        => Skip = MetadataLivePrerequisites.MissingReason(Configuration)
                  ?? DmlLiveSkip.ForDifferentialRun(
                      Environment.GetEnvironmentVariable(DmlLiveSkip.ConfirmUrlVariable),
                      Environment.GetEnvironmentVariable(DmlLiveSkip.CleanupOnlyVariable));
}

/// <summary>The cleanup-only operation: runs only when PPDS_DML_LIVE_CLEANUP_ONLY=1 (and the same prerequisites).</summary>
public sealed class DmlLiveCleanupFactAttribute : FactAttribute
{
    private static readonly LiveTestConfiguration Configuration = new();

    public DmlLiveCleanupFactAttribute()
        => Skip = MetadataLivePrerequisites.MissingReason(Configuration)
                  ?? DmlLiveSkip.ForCleanup(
                      Environment.GetEnvironmentVariable(DmlLiveSkip.ConfirmUrlVariable),
                      Environment.GetEnvironmentVariable(DmlLiveSkip.CleanupOnlyVariable));
}

/// <summary>Why a DML live operation does not run (null when it runs).</summary>
public static class DmlLiveSkip
{
    public const string ConfirmUrlVariable = "PPDS_DML_LIVE_CONFIRM_URL";
    public const string CleanupOnlyVariable = "PPDS_DML_LIVE_CLEANUP_ONLY";

    public static string? ForDifferentialRun(string? confirmUrl, string? cleanupOnly)
    {
        if (string.IsNullOrWhiteSpace(confirmUrl))
        {
            return $"{ConfirmUrlVariable} is not set: the DML live differential run changes data, so it runs only against the " +
                   "organisation named there (the same URL as DATAVERSE_URL).";
        }
        return cleanupOnly == "1"
            ? $"{CleanupOnlyVariable}=1 asks for the cleanup operation only (CleanupOnly_RemovesOnlyThisSuitesLeftovers); no differential case runs."
            : null;
    }

    public static string? ForCleanup(string? confirmUrl, string? cleanupOnly)
    {
        if (cleanupOnly != "1")
            return $"The cleanup-only operation runs only when {CleanupOnlyVariable}=1.";
        return string.IsNullOrWhiteSpace(confirmUrl)
            ? $"{ConfirmUrlVariable} is not set: cleanup runs only against the organisation named there."
            : null;
    }
}

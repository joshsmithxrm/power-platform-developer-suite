using System;

namespace PPDS.Dataverse.Metadata;

/// <summary>
/// Thrown when a global choice (option set) requested by name does not exist in any spelling: Dataverse
/// rejected the name with a "not found" fault and the published catalog holds no choice by that name (#1759).
/// </summary>
/// <remarks>
/// A read that fails any other way, or whose catalog lookup fails, still surfaces the original fault.
/// The CLI maps this exception to its not-found error code and exit code.
/// </remarks>
public sealed class OptionSetNotFoundException : Exception
{
    /// <summary>Gets the global choice name that was requested.</summary>
    public string OptionSetName { get; }

    /// <summary>
    /// Initializes a new instance for the requested <paramref name="optionSetName"/>.
    /// </summary>
    /// <param name="optionSetName">The global choice name that was requested.</param>
    /// <param name="innerException">The Dataverse fault that rejected the name.</param>
    public OptionSetNotFoundException(string optionSetName, Exception? innerException = null)
        : base($"Global choice '{optionSetName}' not found.", innerException)
    {
        OptionSetName = optionSetName;
    }
}

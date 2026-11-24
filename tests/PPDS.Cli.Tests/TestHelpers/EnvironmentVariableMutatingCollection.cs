using Xunit;

namespace PPDS.Cli.Tests.TestHelpers;

/// <summary>
/// Serializes tests that set process-wide environment variables such as <c>PPDS_CONFIG_DIR</c> or
/// <c>HOME</c>. Without this, xUnit runs test classes in parallel and a test in another class reads
/// the value one of these tests set. Tag every class that calls
/// <see cref="System.Environment.SetEnvironmentVariable(string, string)"/> for one of these variables with
/// <c>[Collection(EnvironmentVariableMutatingCollection.Name)]</c>, and restore the previous value in
/// <c>Dispose</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class EnvironmentVariableMutatingCollection
{
    /// <summary>Collection name.</summary>
    public const string Name = nameof(EnvironmentVariableMutatingCollection);
}

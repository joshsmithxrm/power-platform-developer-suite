using Xunit;

namespace PPDS.Auth.Tests;

/// <summary>
/// Serializes tests that mutate process-global environment variables
/// (e.g. PPDS_SPN_SECRET, PPDS_TEST_CLIENT_SECRET).
/// Without this, xUnit runs test classes in parallel and concurrent env var
/// mutation causes intermittent failures — particularly on net8.0 where
/// thread-pool scheduling surfaces the race more readily.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class EnvironmentVariableMutatingCollection
{
    public const string Name = nameof(EnvironmentVariableMutatingCollection);
}

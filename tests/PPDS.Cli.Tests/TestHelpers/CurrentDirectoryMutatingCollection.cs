using Xunit;

namespace PPDS.Cli.Tests.TestHelpers;

/// <summary>
/// Serializes tests that mutate process-global <see cref="System.IO.Directory.SetCurrentDirectory(string)"/>.
/// Without this, xUnit runs test classes in parallel by default and concurrent CWD mutation races
/// (one test captures another test's sandbox directory as its "original", then fails on Dispose when
/// the sandbox is deleted). Tag every class that calls <c>Directory.SetCurrentDirectory</c> with
/// <c>[Collection(nameof(CurrentDirectoryMutatingCollection))]</c>.
/// </summary>
[CollectionDefinition(nameof(CurrentDirectoryMutatingCollection), DisableParallelization = true)]
public class CurrentDirectoryMutatingCollection
{
}

using Xunit;

namespace PPDS.Cli.Tests.TestHelpers;

/// <summary>
/// Serializes tests that swap the process-global console writers via
/// <see cref="System.Console.SetOut(System.IO.TextWriter)"/> / <see cref="System.Console.SetError(System.IO.TextWriter)"/>.
/// Without this, xUnit runs test classes in parallel by default and concurrent swaps race:
/// one class restores the original writer while another is still capturing, so output lands in
/// the wrong <see cref="System.IO.StringWriter"/> (observed flaky on
/// <c>FetchCommand_WriteCsvOutput_EmptyResult_EmitsHeaderNotZeroBytes</c>, #1336; same hazard
/// documented on <c>CommandAliasDeprecation.WarnIfDeprecatedAliasUsed</c>). Tag every class that
/// calls <c>Console.SetOut</c>/<c>Console.SetError</c> with
/// <c>[Collection(nameof(ConsoleCaptureCollection))]</c> — or better, where the code under test
/// accepts an injectable <see cref="System.IO.TextWriter"/>, assert on an isolated writer instead
/// of swapping the console at all (see <c>CommandAliasDeprecationTests</c>).
/// </summary>
[CollectionDefinition(nameof(ConsoleCaptureCollection), DisableParallelization = true)]
public class ConsoleCaptureCollection
{
}

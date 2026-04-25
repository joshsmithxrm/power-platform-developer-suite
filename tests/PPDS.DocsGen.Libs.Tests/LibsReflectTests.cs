using FluentAssertions;
using PPDS.DocsGen.Common;
using PPDS.DocsGen.Libs;
using Xunit;

namespace PPDS.DocsGen.Libs.Tests;

public sealed class LibsReflectTests
{
    private static string FixtureAssemblyPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "FixtureAssembly");
        var dll = Path.Combine(dir, "PPDS.DocsGen.Libs.Tests.FixtureLib.dll");
        File.Exists(dll).Should().BeTrue("the fixture assembly should have been copied into FixtureAssembly/ by the csproj post-build target");
        return dll;
    }

    private static string ExpectedRoot()
    {
        // tests/PPDS.DocsGen.Libs.Tests/bin/Debug/net8.0/Fixtures/Expected
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", "Expected");
    }

    private static async Task<GenerationResult> RunAsync(string outputRoot)
    {
        var generator = new LibraryReferenceGenerator();
        return await generator.GenerateAsync(
            new GenerationInput(FixtureAssemblyPath(), outputRoot),
            CancellationToken.None);
    }

    [Fact]
    public async Task EmitsOnlyDocumentedCustomerFacingTypes()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "libs-reflect-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(outputRoot);

            // Assert: no file is emitted for the [EditorBrowsable(Never)] type or the undocumented type.
            result.Files.Select(f => f.RelativePath)
                .Should().NotContain(p => p.EndsWith("HiddenHelper.md"));
            result.Files.Select(f => f.RelativePath)
                .Should().NotContain(p => p.EndsWith("UndocumentedThing.md"));

            // Assert: files for the two documented customer-facing types ARE emitted.
            result.Files.Should().ContainSingle(f => f.RelativePath.EndsWith("/Widget.md"),
                "Widget is a fully-documented public type");
            result.Files.Should().ContainSingle(f => f.RelativePath.EndsWith("/IWidget.md"),
                "IWidget is a fully-documented public interface");

            // Golden-file byte-for-byte comparison (AC-16).
            var expectedRoot = ExpectedRoot();
            foreach (var file in result.Files)
            {
                var expectedPath = Path.Combine(expectedRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                File.Exists(expectedPath)
                    .Should().BeTrue($"expected golden file missing for {file.RelativePath} at {expectedPath}");

                var expected = await File.ReadAllTextAsync(expectedPath);
                // Normalize line endings: expected files are committed with LF, generator emits LF.
                var expectedNormalized = expected.Replace("\r\n", "\n");
                file.Contents.Replace("\r\n", "\n")
                    .Should().Be(expectedNormalized, $"generated output for {file.RelativePath} must match the golden file byte-for-byte");
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EmitsDiagnosticForUndocumentedType()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "libs-reflect-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(outputRoot);

            result.Diagnostics.Should().Contain(d => d.Contains("UndocumentedThing") && d.Contains("skipping"));
            result.Files.Should().NotContain(f => f.RelativePath.EndsWith("UndocumentedThing.md"));
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UnresolvableInheritdoc_EmitsFallbackPointerAndDiagnostic()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "libs-reflect-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(outputRoot);

            // The fixture's ExternalDocsConsumer.DoExternalWork uses <inheritdoc />
            // against an external cref that is not present in the XML map — the
            // member must still be rendered with a fallback pointer, not dropped.
            var consumerPage = result.Files.SingleOrDefault(f => f.RelativePath.EndsWith("/ExternalDocsConsumer.md"));
            consumerPage.Should().NotBeNull("fallback inheritdoc must still produce a member page");
            consumerPage!.Contents.Should().Contain("DoExternalWork",
                "the member with unresolvable inheritdoc must still render");
            consumerPage.Contents.Should().Contain("*(inherited from `External.Unresolvable.Api.SomeThing`)*",
                "the fallback pointer must surface the cref the author specified");

            result.Diagnostics.Should().Contain(
                d => d.Contains("DoExternalWork") && d.Contains("<inheritdoc") && d.Contains("unresolvable"),
                "a distinct diagnostic must flag the unresolvable inheritdoc — separate from the missing-summary category");
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SkipsEditorBrowsableNeverSilently()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "libs-reflect-eb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(outputRoot);

            // No file is emitted for the [EditorBrowsable(Never)] type.
            result.Files.Should().NotContain(f => f.RelativePath.EndsWith("HiddenHelper.md"),
                "types marked [EditorBrowsable(Never)] are intentionally hidden from generated reference");

            // And no diagnostic is logged — hiding is intentional, not a bug.
            result.Diagnostics.Should().NotContain(d => d.Contains("HiddenHelper"),
                "hiding via [EditorBrowsable(Never)] is not an authoring error, so no diagnostic is surfaced");
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeterministicOutputAcrossRuns()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "libs-reflect-det-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await RunAsync(outputRoot);
            var second = await RunAsync(outputRoot);

            first.Files.Count.Should().Be(second.Files.Count);
            for (var i = 0; i < first.Files.Count; i++)
            {
                first.Files[i].RelativePath.Should().Be(second.Files[i].RelativePath);
                first.Files[i].Contents.Should().Be(second.Files[i].Contents, $"run 1 and run 2 must produce byte-identical output for {first.Files[i].RelativePath}");
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RelativePaths_DoNotIncludeOutputDirectoryPrefix()
    {
        // Regression: RelativePath used to include "docs/reference/libraries/" which caused
        // path duplication when Program.cs did Path.Combine(outputRoot, file.RelativePath)
        // with --output set to "artifacts/docs/reference/libraries".
        var outputRoot = Path.Combine(Path.GetTempPath(), "libs-reflect-relpath-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(outputRoot);

            result.Files.Should().NotBeEmpty("at least one file should be generated");

            foreach (var file in result.Files)
            {
                file.RelativePath.Should().NotStartWith("docs/",
                    $"RelativePath must be relative to the output directory, not include site-level prefix — got '{file.RelativePath}'");
                file.RelativePath.Should().NotStartWith("/",
                    $"RelativePath must not be absolute — got '{file.RelativePath}'");
                Path.IsPathRooted(file.RelativePath).Should().BeFalse(
                    $"RelativePath must not be rooted — got '{file.RelativePath}'");
                file.RelativePath.Should().StartWith("DocsGen.Libs.Tests.FixtureLib/",
                    $"RelativePath must start with the package directory — got '{file.RelativePath}'");
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }
}

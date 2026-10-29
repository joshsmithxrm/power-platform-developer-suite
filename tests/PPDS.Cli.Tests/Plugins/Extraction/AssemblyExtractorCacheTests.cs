using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Extraction;

/// <summary>
/// Exercises reference-cache recovery in isolated directories, without relying on a
/// framework installation or changing the process-wide resolver cache.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class AssemblyExtractorCacheTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), $"ppds-cache-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
            Directory.Delete(_baseDir, recursive: true);
    }

    [Theory]
    [InlineData("System.Runtime.dll")]
    [InlineData("System.Linq.dll")]
    [InlineData("System.dll")]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-52")]
    public void GetNet462ReferenceAssemblyDirectory_MissingNonCoreAssembly_RestoresExactContent(string fileName)
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        Assert.NotNull(cache);
        var path = Path.Combine(cache, fileName);
        var expected = File.ReadAllBytes(path);
        File.Delete(path);

        // A count-only check must not be satisfied by an unrelated file replacing the facade.
        File.WriteAllBytes(Path.Combine(cache, "unrelated.dll"), expected);
        Assert.True(File.Exists(Path.Combine(cache, "mscorlib.dll")));
        Assert.True(File.Exists(Path.Combine(cache, AssemblyExtractor.CompletionSentinelFileName)));

        var recovered = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);

        Assert.Equal(cache, recovered);
        Assert.True(File.Exists(path), $"Missing reference assembly {fileName} must be restored.");
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateDirectories(_baseDir, ".staging-*"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-52")]
    public void GetNet462ReferenceAssemblyDirectory_TruncatedFacade_RestoresExactContent(int length)
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        Assert.NotNull(cache);
        var path = Path.Combine(cache, "System.Runtime.dll");
        var expected = File.ReadAllBytes(path);
        File.WriteAllBytes(path, expected[..length]);

        var recovered = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);

        Assert.Equal(cache, recovered);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-53")]
    public void GetNet462ReferenceAssemblyDirectory_ValidCache_PreservesExistingFiles()
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        Assert.NotNull(cache);
        var paths = Directory.GetFiles(cache);
        var timestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (var path in paths)
            File.SetLastWriteTimeUtc(path, timestamp);
        var timestamps = paths.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var marker = Path.Combine(cache, "reuse-marker.txt");
        File.WriteAllText(marker, "preserve the existing cache");

        var reused = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);

        Assert.Equal(cache, reused);
        Assert.Equal("preserve the existing cache", File.ReadAllText(marker));
        Assert.All(timestamps, pair => Assert.Equal(pair.Value, File.GetLastWriteTimeUtc(pair.Key)));
        Assert.Empty(Directory.EnumerateDirectories(_baseDir, ".staging-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-54")]
    public async Task GetNet462ReferenceAssemblyDirectory_ConcurrentCalls_ReturnCompleteCache(bool corruptExistingCache)
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        Assert.NotNull(cache);
        var expected = Directory.GetFiles(cache, "*.dll")
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        if (corruptExistingCache)
            File.Delete(Path.Combine(cache, "System.Runtime.dll"));
        else
            Directory.Delete(cache, recursive: true);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var result = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
            Assert.Equal(cache, result);
            Assert.All(expected, pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(result!, pair.Key!))));
            return result;
        })).ToArray();
        start.SetResult();

        await Task.WhenAll(tasks);

        Assert.Empty(Directory.EnumerateDirectories(_baseDir, ".staging-*"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-54")]
    public async Task GetNet462ReferenceAssemblyDirectory_StaleCacheMiss_PreservesConcurrentWinner()
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        Assert.NotNull(cache);
        File.Delete(Path.Combine(cache, "System.Runtime.dll"));
        using var missDetected = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();

        var delayed = Task.Run(() => AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir, () =>
        {
            missDetected.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)), "Competing recovery did not finish.");
        }));

        try
        {
            Assert.True(missDetected.Wait(TimeSpan.FromSeconds(10)), "Expected the delayed caller to observe a cache miss.");
            Assert.Equal(cache, AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir));
            File.WriteAllText(Path.Combine(cache, "winner-marker.txt"), "preserve the concurrent winner");
        }
        finally
        {
            resume.Set();
            Assert.Equal(cache, await delayed);
        }

        Assert.Equal("preserve the concurrent winner", File.ReadAllText(Path.Combine(cache, "winner-marker.txt")));
        Assert.True(File.Exists(Path.Combine(cache, "System.Runtime.dll")));
        Assert.Empty(Directory.EnumerateDirectories(_baseDir, ".staging-*"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("AC", "AC-55")]
    public void GetNet462ReferenceAssemblyDirectory_BlockedPublication_CleansStagingAndCanRetry()
    {
        var cache = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        Assert.NotNull(cache);
        Directory.Delete(cache, recursive: true);
        File.WriteAllText(cache, "blocked cache destination");

        using var stderr = new StringWriter();
        using var stdout = new StringWriter();
        var originalError = Console.Error;
        var originalOut = Console.Out;
        string? failed;
        try
        {
            Console.SetError(stderr);
            Console.SetOut(stdout);
            failed = AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir);
        }
        finally
        {
            Console.SetError(originalError);
            Console.SetOut(originalOut);
        }

        Assert.Null(failed);
        Assert.Contains("Warning: failed to extract embedded .NET Framework 4.6.2 reference assemblies", stderr.ToString());
        Assert.Empty(stdout.ToString());
        Assert.Equal("blocked cache destination", File.ReadAllText(cache));
        Assert.Empty(Directory.EnumerateDirectories(_baseDir, ".staging-*"));

        File.Delete(cache);
        Assert.Equal(cache, AssemblyExtractor.GetNet462ReferenceAssemblyDirectory(_baseDir));
        Assert.True(File.Exists(Path.Combine(cache, "System.Runtime.dll")));
    }
}

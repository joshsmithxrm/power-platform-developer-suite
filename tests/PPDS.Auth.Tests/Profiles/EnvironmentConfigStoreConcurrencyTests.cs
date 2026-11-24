using System.Text.Json;
using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

/// <summary>
/// Two store instances on one file stand for two processes (the daemon and the CLI, or the TUI and the CLI):
/// each has its own in-process lock, so only the cross-process lock on <c>environments.json.lock</c> keeps them
/// apart. A raise saved by one must survive a read-modify-write by the other, a reader must never see part of
/// a write, and a lock that cannot be had gives up with a clear error (#1829, review round 3).
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentConfigStoreConcurrencyTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";
    private const string Other = "https://other.crm.dynamics.com";

    private readonly string _dir;
    private readonly string _path;

    public EnvironmentConfigStoreConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ppds-envlock-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "environments.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The file as a fresh store reads it (no cache of the stores under test).</summary>
    private EnvironmentConfigCollection FileState()
    {
        using var fresh = new EnvironmentConfigStore(_path);
        return fresh.ReloadAsync().GetAwaiter().GetResult();
    }

    private EnvironmentType? FileType(string url)
        => FileState().Environments.Single(e => EnvironmentConfig.NormalizeUrl(e.Url) == EnvironmentConfig.NormalizeUrl(url)).Type;

    /// <summary>
    /// The reviewer's interleaving: A (a daemon tagging another environment) has read the file; B (the CLI) raises
    /// the environment from Development to Production; A writes. B must wait for A's lock and then apply its raise
    /// to what A wrote, so the raise survives.
    /// </summary>
    [Fact]
    public async Task AReads_BRaises_AWrites_TheRaiseSurvives()
    {
        using var a = new EnvironmentConfigStore(_path);
        using var b = new EnvironmentConfigStore(_path);
        await b.SaveConfigAsync(Url, type: EnvironmentType.Development);

        Task? raise = null;
        await a.SaveConfigAsync(Other, label: "Other", type: null, color: null, clearColor: false, discoveredType: "Sandbox",
            profileName: "p", validate: (_, _) =>
            {
                // A holds the file between its read and its write. B's raise must wait for it.
                raise = Task.Run(() => b.SaveConfigAsync(Url, type: EnvironmentType.Production));
                raise.Wait(TimeSpan.FromMilliseconds(500)).Should().BeFalse("B must not write while A holds the file");
            });
        await raise!;

        FileType(Url).Should().Be(EnvironmentType.Production);
        FileState().Environments.Should().Contain(e => e.Label == "Other");
    }

    /// <summary>The same interleaving for a removal and for an update: neither can write back what it read before the raise.</summary>
    [Fact]
    public async Task ARemovesOrUpdates_WhileBRaises_TheRaiseSurvives()
    {
        using var a = new EnvironmentConfigStore(_path);
        using var b = new EnvironmentConfigStore(_path);
        await b.SaveConfigAsync(Url, type: EnvironmentType.Development);
        await b.SaveConfigAsync(Other, label: "Other");

        Task? raise = null;
        await a.UpdateAsync(collection =>
        {
            raise = Task.Run(() => b.SaveConfigAsync(Url, type: EnvironmentType.Production));
            raise.Wait(TimeSpan.FromMilliseconds(500)).Should().BeFalse("B must not write while A holds the file");
            collection.TypeDefaults[EnvironmentType.Sandbox] = EnvironmentColor.Blue;
            return true;
        });
        await raise!;
        FileType(Url).Should().Be(EnvironmentType.Production);

        (await a.RemoveConfigAsync(Other)).Should().BeTrue();
        FileType(Url).Should().Be(EnvironmentType.Production);
    }

    /// <summary>A read while another store is between its read and its write waits, then sees the finished write.</summary>
    [Fact]
    public async Task ReadDuringAWrite_WaitsAndSeesTheFinishedWrite()
    {
        using var writer = new EnvironmentConfigStore(_path);
        using var reader = new EnvironmentConfigStore(_path);
        await writer.SaveConfigAsync(Url, type: EnvironmentType.Development);
        (await reader.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development);

        Task<EnvironmentConfig?>? read = null;
        await writer.SaveConfigAsync(Url, label: null, type: EnvironmentType.Production, color: null, clearColor: false,
            discoveredType: null, profileName: null, validate: (_, _) =>
            {
                read = Task.Run(() => reader.GetConfigAsync(Url));
                read.Wait(TimeSpan.FromMilliseconds(500)).Should().BeFalse("the read must wait for the write to finish");
            });

        (await read!)!.Type.Should().Be(EnvironmentType.Production);
    }

    /// <summary>
    /// Readers outside the lock (another program reading the file) never see part of a write: the store writes a
    /// temporary file and moves it over the old one.
    /// </summary>
    [Fact]
    public async Task ReadersNeverSeeAPartialFile_WhileAnotherStoreWrites()
    {
        using var writer = new EnvironmentConfigStore(_path);
        await writer.SaveConfigAsync(Url, label: new string('x', 2000), type: EnvironmentType.Production);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var writes = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
                await writer.SaveConfigAsync(Url, label: new string((char)('a' + i++ % 26), 2000 + i % 500), type: EnvironmentType.Production);
        });
        var reads = 0;
        while (!stop.IsCancellationRequested)
        {
            string json;
            try { json = File.ReadAllText(_path); }
            catch (IOException) { continue; } // Windows: the file is being replaced at this instant
            var parsed = () => JsonDocument.Parse(json).Dispose();
            parsed.Should().NotThrow("a reader must see the old or the new file, never part of one");
            reads++;
        }
        await writes;

        reads.Should().BeGreaterThan(10);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty("no temporary file is left behind");
    }

    /// <summary>A read through another store instance during the writes also always parses (it waits for the lock).</summary>
    [Fact]
    public async Task StoreReadsNeverFail_WhileAnotherStoreWrites()
    {
        using var writer = new EnvironmentConfigStore(_path);
        using var reader = new EnvironmentConfigStore(_path);
        await writer.SaveConfigAsync(Url, type: EnvironmentType.Production);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var writes = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
                await writer.SaveConfigAsync(Other, label: "o" + i++);
        });
        while (!stop.IsCancellationRequested)
            (await reader.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        await writes;
    }

    /// <summary>
    /// A lock held elsewhere for longer than the timeout: the change gives up with a TimeoutException that says
    /// what happened, and nothing is written. Once the holder lets go (as when its process exits and the
    /// operating system closes its handle) the next change succeeds.
    /// </summary>
    [Fact]
    public async Task LockHeldElsewhere_TimesOutWithAClearError_AndWritesNothing_ThenSucceedsOnceReleased()
    {
        using var store = new EnvironmentConfigStore(_path);
        await store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        store.LockTimeout = TimeSpan.FromMilliseconds(300);

        var holder = new FileStream(store.LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var save = () => store.SaveConfigAsync(Url, type: EnvironmentType.Development);
            (await save.Should().ThrowAsync<TimeoutException>())
                .Which.Message.Should().Contain("environment configuration lock").And.Contain("was not read or changed");
            var read = () => store.GetConfigAsync(Url);
            await read.Should().ThrowAsync<TimeoutException>();
            // Read the raw file: any store would wait for the lock this test holds.
            File.ReadAllText(_path).Should().Contain("\"Production\"").And.NotContain("Development");
        }
        finally
        {
            holder.Dispose();
        }

        await store.SaveConfigAsync(Url, label: "after");
        (await store.GetConfigAsync(Url))!.Label.Should().Be("after");
    }

    /// <summary>The lock is a sibling file next to the environments file (it moves with PPDS_CONFIG_DIR) and is never deleted.</summary>
    [Fact]
    public async Task TheLockFile_IsBesideTheEnvironmentsFile_AndStays()
    {
        using var store = new EnvironmentConfigStore(_path);
        await store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        store.LockFilePath.Should().Be(_path + ".lock");
        File.Exists(store.LockFilePath).Should().BeTrue();
    }

    /// <summary>GetConfigAsync reads the file: a raise another store saved is seen at once, with no ClearCache.</summary>
    [Fact]
    public async Task GetConfig_SeesAnotherStoresRaise_WithoutClearCache()
    {
        using var longLived = new EnvironmentConfigStore(_path);
        using var cli = new EnvironmentConfigStore(_path);
        await longLived.SaveConfigAsync(Url, type: EnvironmentType.Development);
        (await longLived.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Development);

        await cli.SaveConfigAsync(Url, type: EnvironmentType.Production);

        (await longLived.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Production);
        (await longLived.ReloadAsync()).Environments.Single().Type.Should().Be(EnvironmentType.Production);
    }
}

using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

public class ProfileStoreTests : IDisposable
{
    private readonly string _tempFilePath;
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"test_profiles_{Guid.NewGuid()}.json");
        _store = new ProfileStore(_tempFilePath);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
    }

    [Fact]
    public async Task LoadAsync_FileDoesNotExist_ReturnsEmptyCollection()
    {
        var collection = await _store.LoadAsync();

        collection.Should().NotBeNull();
        collection.Count.Should().Be(0);
    }

    [Fact]
    public void Load_FileDoesNotExist_ReturnsEmptyCollection()
    {
        var collection = _store.Load();

        collection.Should().NotBeNull();
        collection.Count.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_CreatesFile()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });

        await _store.SaveAsync(collection);

        File.Exists(_tempFilePath).Should().BeTrue();
    }

    [Fact]
    public void Save_CreatesFile()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });

        _store.Save(collection);

        File.Exists(_tempFilePath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_LoadAsync_RoundTrip()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        });

        await _store.SaveAsync(collection);
        _store.ClearCache();
        var loaded = await _store.LoadAsync();

        loaded.Count.Should().Be(1);
        loaded.All.First().Name.Should().Be("test");
        loaded.All.First().AuthMethod.Should().Be(AuthMethod.ClientSecret);
        loaded.All.First().ApplicationId.Should().Be("app-id");
        loaded.All.First().TenantId.Should().Be("tenant-id");
    }

    [Fact]
    public void Save_Load_RoundTrip()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile
        {
            Name = "test",
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id"
        });

        _store.Save(collection);
        _store.ClearCache();
        var loaded = _store.Load();

        loaded.Count.Should().Be(1);
        loaded.All.First().Name.Should().Be("test");
    }

    [Fact]
    public async Task SaveAsync_PreservesActiveProfile()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "first" });
        collection.Add(new AuthProfile { Name = "second" }, setAsActive: true);

        await _store.SaveAsync(collection);
        _store.ClearCache();
        var loaded = await _store.LoadAsync();

        loaded.ActiveProfile!.Name.Should().Be("second");
        loaded.ActiveProfileIndex.Should().Be(2);
        loaded.ActiveProfileName.Should().Be("second");
    }

    [Fact]
    public async Task LoadAsync_CachesResult()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });
        await _store.SaveAsync(collection);

        var first = await _store.LoadAsync();
        var second = await _store.LoadAsync();

        first.Should().BeSameAs(second);
    }

    [Fact]
    public async Task ClearCache_ForcesReload()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });
        await _store.SaveAsync(collection);

        var first = await _store.LoadAsync();
        _store.ClearCache();
        var second = await _store.LoadAsync();

        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public async Task SaveAsync_UpdatesCache()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });

        await _store.SaveAsync(collection);
        var loaded = await _store.LoadAsync();

        loaded.Should().BeSameAs(collection);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });
        _store.Save(collection);

        _store.Delete();

        File.Exists(_tempFilePath).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ClearsCache()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });
        await _store.SaveAsync(collection);

        _store.Delete();
        var loaded = await _store.LoadAsync();

        loaded.Count.Should().Be(0);
    }

    [Fact]
    public void Delete_FileDoesNotExist_DoesNotThrow()
    {
        var act = () => _store.Delete();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SaveAsync_NullCollection_Throws()
    {
        var act = async () => await _store.SaveAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Save_NullCollection_Throws()
    {
        var act = () => _store.Save(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_PreservesEnvironmentInfo()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile
        {
            Name = "test",
            Environment = EnvironmentInfo.Create("https://test.crm.dynamics.com", "Test Env")
        });

        await _store.SaveAsync(collection);
        _store.ClearCache();
        var loaded = await _store.LoadAsync();

        var env = loaded.All.First().Environment;
        env.Should().NotBeNull();
        env!.Url.Should().Be("https://test.crm.dynamics.com");
        env.DisplayName.Should().Be("Test Env");
    }

    [Fact]
    public async Task SaveAsync_SetsVersionToTwo()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });

        await _store.SaveAsync(collection);
        var json = await File.ReadAllTextAsync(_tempFilePath);

        json.Should().Contain("\"version\": 2");
    }

    [Fact]
    public async Task SaveAsync_UsesArrayForProfiles()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test1" });
        collection.Add(new AuthProfile { Name = "test2" });

        await _store.SaveAsync(collection);
        var json = await File.ReadAllTextAsync(_tempFilePath);

        // v2 uses array format with "profiles": [...]
        json.Should().Contain("\"profiles\": [");
    }

    [Fact]
    public async Task SaveAsync_UsesActiveProfileIndexAndName()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "myprofile" });

        await _store.SaveAsync(collection);
        var json = await File.ReadAllTextAsync(_tempFilePath);

        json.Should().Contain("\"activeProfileIndex\": 1");
        json.Should().Contain("\"activeProfile\": \"myprofile\"");
    }

    [Fact]
    public async Task SaveAsync_PreservesNewFields()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile
        {
            Name = "test",
            Authority = "https://login.microsoftonline.com/tenant-id"
        });

        await _store.SaveAsync(collection);
        _store.ClearCache();
        var loaded = await _store.LoadAsync();

        var profile = loaded.All.First();
        profile.Authority.Should().Be("https://login.microsoftonline.com/tenant-id");
    }

    [Fact]
    public async Task LoadAsync_V1Schema_DeletesAndReturnsEmpty()
    {
        // Write a v1 schema file directly
        var v1Json = """
            {
              "version": 1,
              "activeIndex": 1,
              "profiles": {
                "1": {
                  "index": 1,
                  "name": "test",
                  "authMethod": "clientSecret",
                  "applicationId": "app-id",
                  "clientSecret": "secret"
                }
              }
            }
            """;
        await File.WriteAllTextAsync(_tempFilePath, v1Json);

        var loaded = await _store.LoadAsync();

        // v1 profiles should be deleted
        File.Exists(_tempFilePath).Should().BeFalse();
        loaded.Count.Should().Be(0);
    }

    [Fact]
    public void Load_V1Schema_DeletesAndReturnsEmpty()
    {
        // Write a v1 schema file directly
        var v1Json = """
            {
              "version": 1,
              "activeIndex": 1,
              "profiles": {
                "1": {
                  "index": 1,
                  "name": "test"
                }
              }
            }
            """;
        File.WriteAllText(_tempFilePath, v1Json);

        var loaded = _store.Load();

        // v1 profiles should be deleted
        File.Exists(_tempFilePath).Should().BeFalse();
        loaded.Count.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_V1DetectedByActiveIndex_DeletesFile()
    {
        // v1 is detected by presence of "activeIndex" property
        var v1Json = """
            {
              "activeIndex": 1,
              "profiles": []
            }
            """;
        await File.WriteAllTextAsync(_tempFilePath, v1Json);

        var loaded = await _store.LoadAsync();

        File.Exists(_tempFilePath).Should().BeFalse();
        loaded.Count.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_V1DetectedByDictionaryProfiles_DeletesFile()
    {
        // v1 profiles is an object (dictionary), v2 is an array
        var v1Json = """
            {
              "profiles": {
                "1": { "index": 1, "name": "test" }
              }
            }
            """;
        await File.WriteAllTextAsync(_tempFilePath, v1Json);

        var loaded = await _store.LoadAsync();

        File.Exists(_tempFilePath).Should().BeFalse();
        loaded.Count.Should().Be(0);
    }
}

using System.Text;
using FluentAssertions;
using PPDS.Migration.Formats;
using Xunit;

namespace PPDS.Migration.Tests.Formats;

public class UserMappingReaderTests
{
    [Fact]
    public async Task ReadAsync_ParsesUserMappings()
    {
        var sourceId1 = Guid.NewGuid();
        var targetId1 = Guid.NewGuid();
        var sourceId2 = Guid.NewGuid();
        var targetId2 = Guid.NewGuid();

        var xml = $@"<?xml version=""1.0""?>
<userMappings>
  <mapping sourceId=""{sourceId1}"" sourceName=""source1@example.com"" targetId=""{targetId1}"" targetName=""target1@example.com"" />
  <mapping sourceId=""{sourceId2}"" sourceName=""source2@example.com"" targetId=""{targetId2}"" targetName=""target2@example.com"" />
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.Mappings.Should().HaveCount(2);
        collection.Mappings[sourceId1].SourceUserId.Should().Be(sourceId1);
        collection.Mappings[sourceId1].SourceUserName.Should().Be("source1@example.com");
        collection.Mappings[sourceId1].TargetUserId.Should().Be(targetId1);
        collection.Mappings[sourceId1].TargetUserName.Should().Be("target1@example.com");
    }

    [Fact]
    public async Task ReadAsync_ParsesDefaultUserId()
    {
        var defaultId = Guid.NewGuid();

        var xml = $@"<?xml version=""1.0""?>
<userMappings defaultUserId=""{defaultId}"">
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.DefaultUserId.Should().Be(defaultId);
    }

    [Fact]
    public async Task ReadAsync_ParsesUseCurrentUserAsDefault()
    {
        var xml = @"<?xml version=""1.0""?>
<userMappings useCurrentUserAsDefault=""false"">
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.UseCurrentUserAsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_UseCurrentUserAsDefault_DefaultsToTrue()
    {
        var xml = @"<?xml version=""1.0""?>
<userMappings>
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.UseCurrentUserAsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_SkipsInvalidMappings()
    {
        var validSourceId = Guid.NewGuid();
        var validTargetId = Guid.NewGuid();

        var xml = $@"<?xml version=""1.0""?>
<userMappings>
  <mapping sourceId=""{validSourceId}"" targetId=""{validTargetId}"" />
  <mapping sourceId=""invalid-guid"" targetId=""{Guid.NewGuid()}"" />
  <mapping sourceId=""{Guid.NewGuid()}"" targetId=""invalid-guid"" />
  <mapping targetId=""{Guid.NewGuid()}"" />
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.Mappings.Should().HaveCount(1);
        collection.Mappings[validSourceId].TargetUserId.Should().Be(validTargetId);
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenStreamIsNull()
    {
        var reader = new UserMappingReader();

        var act = async () => await reader.ReadAsync((Stream)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenPathIsNull()
    {
        var reader = new UserMappingReader();

        var act = async () => await reader.ReadAsync((string)null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenFileNotFound()
    {
        var reader = new UserMappingReader();

        var act = async () => await reader.ReadAsync("nonexistent.xml");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ReadAsync_HandlesUseCurrentUserAsDefault_WithValue1()
    {
        var xml = @"<?xml version=""1.0""?>
<userMappings useCurrentUserAsDefault=""1"">
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.UseCurrentUserAsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_HandlesEmptyMappings()
    {
        var xml = @"<?xml version=""1.0""?>
<userMappings>
</userMappings>";

        var reader = new UserMappingReader();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var collection = await reader.ReadAsync(stream);

        collection.Mappings.Should().BeEmpty();
        collection.DefaultUserId.Should().BeNull();
        collection.UseCurrentUserAsDefault.Should().BeTrue();
    }
}

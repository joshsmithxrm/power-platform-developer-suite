using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

public class ProfileCollectionTests
{
    [Fact]
    public void Add_FirstProfile_SetsAsActive()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test" };

        collection.Add(profile);

        collection.ActiveProfile.Should().Be(profile);
        collection.ActiveProfileIndex.Should().Be(profile.Index);
        collection.ActiveProfileName.Should().Be(profile.Name);
    }

    [Fact]
    public void Add_SecondProfile_DoesNotChangeActive()
    {
        var collection = new ProfileCollection();
        var first = new AuthProfile { Name = "first" };
        var second = new AuthProfile { Name = "second" };
        collection.Add(first);

        collection.Add(second);

        collection.ActiveProfile.Should().Be(first);
    }

    [Fact]
    public void Add_WithSetAsActiveTrue_SetsAsActive()
    {
        var collection = new ProfileCollection();
        var first = new AuthProfile { Name = "first" };
        var second = new AuthProfile { Name = "second" };
        collection.Add(first);

        collection.Add(second, setAsActive: true);

        collection.ActiveProfile.Should().Be(second);
    }

    [Fact]
    public void Add_AutoAssignsIndex()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test" };

        collection.Add(profile);

        profile.Index.Should().Be(1);
    }

    [Fact]
    public void Add_AutoAssignsIncrementingIndexes()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "test1" };
        var profile2 = new AuthProfile { Name = "test2" };

        collection.Add(profile1);
        collection.Add(profile2);

        profile1.Index.Should().Be(1);
        profile2.Index.Should().Be(2);
    }

    [Fact]
    public void Add_DuplicateIndex_Throws()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "test1", Index = 5 };
        var profile2 = new AuthProfile { Name = "test2", Index = 5 };
        collection.Add(profile1);

        var act = () => collection.Add(profile2);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public void Count_ReturnsCorrectCount()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "first" });
        collection.Add(new AuthProfile { Name = "second" });

        collection.Count.Should().Be(2);
    }

    [Fact]
    public void All_ReturnsProfilesInIndexOrder()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "first", Index = 5 };
        var profile2 = new AuthProfile { Name = "second", Index = 2 };
        var profile3 = new AuthProfile { Name = "third", Index = 10 };
        collection.Add(profile1);
        collection.Add(profile2);
        collection.Add(profile3);

        var all = collection.All.ToList();

        all.Should().HaveCount(3);
        all[0].Should().Be(profile2);
        all[1].Should().Be(profile1);
        all[2].Should().Be(profile3);
    }

    [Fact]
    public void GetByIndex_ReturnsProfile()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test", Index = 5 };
        collection.Add(profile);

        var result = collection.GetByIndex(5);

        result.Should().Be(profile);
    }

    [Fact]
    public void GetByIndex_NotFound_ReturnsNull()
    {
        var collection = new ProfileCollection();

        var result = collection.GetByIndex(999);

        result.Should().BeNull();
    }

    [Fact]
    public void GetByName_ReturnsProfile_CaseInsensitive()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "TestProfile" };
        collection.Add(profile);

        var result = collection.GetByName("testprofile");

        result.Should().Be(profile);
    }

    [Fact]
    public void GetByName_NotFound_ReturnsNull()
    {
        var collection = new ProfileCollection();

        var result = collection.GetByName("nonexistent");

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void GetByName_NullOrWhitespace_ReturnsNull(string? name)
    {
        var collection = new ProfileCollection();

        var result = collection.GetByName(name!);

        result.Should().BeNull();
    }

    [Fact]
    public void GetByNameOrIndex_WithValidIndex_ReturnsProfile()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test", Index = 5 };
        collection.Add(profile);

        var result = collection.GetByNameOrIndex("5");

        result.Should().Be(profile);
    }

    [Fact]
    public void GetByNameOrIndex_WithValidName_ReturnsProfile()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test" };
        collection.Add(profile);

        var result = collection.GetByNameOrIndex("test");

        result.Should().Be(profile);
    }

    [Fact]
    public void GetByNameOrIndex_IndexTakesPrecedence()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "5", Index = 1 };
        var profile2 = new AuthProfile { Name = "test", Index = 5 };
        collection.Add(profile1);
        collection.Add(profile2);

        var result = collection.GetByNameOrIndex("5");

        result.Should().Be(profile2);
    }

    [Fact]
    public void RemoveByIndex_RemovesProfile()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test", Index = 5 };
        collection.Add(profile);

        var result = collection.RemoveByIndex(5);

        result.Should().BeTrue();
        collection.Count.Should().Be(0);
    }

    [Fact]
    public void RemoveByIndex_NotFound_ReturnsFalse()
    {
        var collection = new ProfileCollection();

        var result = collection.RemoveByIndex(999);

        result.Should().BeFalse();
    }

    [Fact]
    public void RemoveByIndex_ActiveProfile_ClearsActiveWhenEmpty()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test" };
        collection.Add(profile);

        collection.RemoveByIndex(profile.Index);

        collection.ActiveProfileIndex.Should().BeNull();
        collection.ActiveProfileName.Should().BeNull();
        collection.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public void RemoveByIndex_ActiveProfile_SelectsFirstRemainingProfile()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "first", Index = 5 };
        var profile2 = new AuthProfile { Name = "second", Index = 10 };
        collection.Add(profile1);
        collection.Add(profile2);

        collection.RemoveByIndex(5);

        collection.ActiveProfile.Should().Be(profile2);
        collection.ActiveProfileIndex.Should().Be(10);
        collection.ActiveProfileName.Should().Be("second");
    }

    [Fact]
    public void RemoveByName_RemovesProfile()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test" };
        collection.Add(profile);

        var result = collection.RemoveByName("test");

        result.Should().BeTrue();
        collection.Count.Should().Be(0);
    }

    [Fact]
    public void RemoveByName_CaseInsensitive()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "TestProfile" };
        collection.Add(profile);

        var result = collection.RemoveByName("testprofile");

        result.Should().BeTrue();
    }

    [Fact]
    public void RemoveByName_NotFound_ReturnsFalse()
    {
        var collection = new ProfileCollection();

        var result = collection.RemoveByName("nonexistent");

        result.Should().BeFalse();
    }

    [Fact]
    public void SetActiveByIndex_SetsActiveProfile()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "first" };
        var profile2 = new AuthProfile { Name = "second" };
        collection.Add(profile1);
        collection.Add(profile2);

        collection.SetActiveByIndex(profile2.Index);

        collection.ActiveProfile.Should().Be(profile2);
    }

    [Fact]
    public void SetActiveByIndex_NotFound_Throws()
    {
        var collection = new ProfileCollection();

        var act = () => collection.SetActiveByIndex(999);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public void SetActiveByName_SetsActiveProfile()
    {
        var collection = new ProfileCollection();
        var profile1 = new AuthProfile { Name = "first" };
        var profile2 = new AuthProfile { Name = "second" };
        collection.Add(profile1);
        collection.Add(profile2);

        collection.SetActiveByName("second");

        collection.ActiveProfile.Should().Be(profile2);
    }

    [Fact]
    public void SetActiveByName_CaseInsensitive()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "TestProfile" };
        collection.Add(profile);

        collection.SetActiveByName("testprofile");

        collection.ActiveProfile.Should().Be(profile);
    }

    [Fact]
    public void SetActiveByName_NotFound_Throws()
    {
        var collection = new ProfileCollection();

        var act = () => collection.SetActiveByName("nonexistent");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public void Clear_RemovesAllProfilesAndClearsActive()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "first" });
        collection.Add(new AuthProfile { Name = "second" });

        collection.Clear();

        collection.Count.Should().Be(0);
        collection.ActiveProfileIndex.Should().BeNull();
        collection.ActiveProfileName.Should().BeNull();
        collection.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public void IsNameInUse_ExistingName_ReturnsTrue()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });

        var result = collection.IsNameInUse("test");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsNameInUse_CaseInsensitive()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "TestProfile" });

        var result = collection.IsNameInUse("testprofile");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsNameInUse_WithExcludeIndex_ExcludesProfile()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test", Index = 5 };
        collection.Add(profile);

        var result = collection.IsNameInUse("test", excludeIndex: 5);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsNameInUse_NotFound_ReturnsFalse()
    {
        var collection = new ProfileCollection();

        var result = collection.IsNameInUse("nonexistent");

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void IsNameInUse_NullOrWhitespace_ReturnsFalse(string? name)
    {
        var collection = new ProfileCollection();

        var result = collection.IsNameInUse(name!);

        result.Should().BeFalse();
    }

    [Fact]
    public void Clone_CreatesDeepCopy()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Name = "test", Username = "user@example.com" };
        collection.Add(profile);

        var clone = collection.Clone();

        clone.Should().NotBeSameAs(collection);
        clone.Count.Should().Be(1);
        clone.ActiveProfileIndex.Should().Be(collection.ActiveProfileIndex);
        clone.ActiveProfileName.Should().Be(collection.ActiveProfileName);
        clone.All.First().Should().NotBeSameAs(profile);
        clone.All.First().Name.Should().Be("test");
    }

    [Fact]
    public void Clone_ModifyingClone_DoesNotAffectOriginal()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Name = "test" });
        var clone = collection.Clone();

        clone.Add(new AuthProfile { Name = "new" });

        collection.Count.Should().Be(1);
        clone.Count.Should().Be(2);
    }

    [Fact]
    public void NextIndex_EmptyCollection_Returns1()
    {
        var collection = new ProfileCollection();

        collection.NextIndex.Should().Be(1);
    }

    [Fact]
    public void NextIndex_WithProfiles_ReturnsMaxPlusOne()
    {
        var collection = new ProfileCollection();
        collection.Add(new AuthProfile { Index = 5 });
        collection.Add(new AuthProfile { Index = 10 });

        collection.NextIndex.Should().Be(11);
    }

    [Fact]
    public void ActiveProfile_EmptyCollection_ReturnsNull()
    {
        var collection = new ProfileCollection();

        collection.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public void ActiveProfile_InvalidActiveProfileName_ReturnsNull()
    {
        var collection = new ProfileCollection();
        // Directly add profile without using Add() which auto-sets ActiveProfileIndex
        collection.Profiles.Add(new AuthProfile { Index = 1, Name = "test" });
        collection.ActiveProfileName = "nonexistent";
        // Don't set ActiveProfileIndex - simulates corrupt/old data with invalid name

        // No fallback to first profile - returns null when active profile not found
        collection.ActiveProfile.Should().BeNull();
    }

    [Fact]
    public void ActiveProfile_LegacyNameOnlyMigration_SetsIndex()
    {
        var collection = new ProfileCollection();
        var profile = new AuthProfile { Index = 3, Name = "legacy-profile" };
        collection.Profiles.Add(profile); // Direct add, no automatic active setting
        collection.ActiveProfileName = "legacy-profile"; // Simulate old profiles.json
        // ActiveProfileIndex is null - simulates migration scenario

        // Accessing ActiveProfile should work and migrate to index-based
        var active = collection.ActiveProfile;

        active.Should().Be(profile);
        collection.ActiveProfileIndex.Should().Be(3); // Index should now be set
    }

    [Fact]
    public void SetActiveByIndex_UnnamedProfile_WorksCorrectly()
    {
        // This is the bug we're fixing - unnamed profiles should work
        var collection = new ProfileCollection();
        var namedProfile = new AuthProfile { Name = "named", Index = 1 };
        var unnamedProfile = new AuthProfile { Name = null, Index = 2 }; // No name
        collection.Add(namedProfile);
        collection.Profiles.Add(unnamedProfile); // Direct add for unnamed

        collection.SetActiveByIndex(2);

        collection.ActiveProfile.Should().Be(unnamedProfile);
        collection.ActiveProfileIndex.Should().Be(2);
        collection.ActiveProfileName.Should().BeNull();
    }

    [Fact]
    public void ActiveProfile_UnnamedProfileByIndex_ReturnsCorrectProfile()
    {
        // Verify that unnamed profiles are found by index
        var collection = new ProfileCollection();
        var unnamedProfile = new AuthProfile { Index = 5, Name = null };
        collection.Profiles.Add(unnamedProfile);
        collection.ActiveProfileIndex = 5;

        collection.ActiveProfile.Should().Be(unnamedProfile);
    }

    [Fact]
    public void Version_DefaultsTo2()
    {
        var collection = new ProfileCollection();

        collection.Version.Should().Be(2);
    }
}

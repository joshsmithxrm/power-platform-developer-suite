using FluentAssertions;
using PPDS.Migration.Constants;
using Xunit;

namespace PPDS.Migration.Tests.Constants;

[Trait("Category", "Unit")]
public class EntityNamesTests
{
    [Fact]
    public void SystemUser_HasCorrectDataverseLogicalName()
    {
        EntityNames.SystemUser.Should().Be("systemuser");
    }

    [Fact]
    public void Team_HasCorrectDataverseLogicalName()
    {
        EntityNames.Team.Should().Be("team");
    }

    [Fact]
    public void BusinessUnit_HasCorrectDataverseLogicalName()
    {
        EntityNames.BusinessUnit.Should().Be("businessunit");
    }
}

[Trait("Category", "Unit")]
public class AttributeNamesTests
{
    [Fact]
    public void IsDefault_HasCorrectDataverseLogicalName()
    {
        AttributeNames.IsDefault.Should().Be("isdefault");
    }
}

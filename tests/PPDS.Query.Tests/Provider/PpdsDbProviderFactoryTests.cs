using System.Data.Common;
using FluentAssertions;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsDbProviderFactoryTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        PpdsDbProviderFactory.Instance.Should().BeSameAs(PpdsDbProviderFactory.Instance);
    }

    [Fact]
    public void Instance_IsDbProviderFactory()
    {
        PpdsDbProviderFactory.Instance.Should().BeAssignableTo<DbProviderFactory>();
    }

    [Fact]
    public void CreateConnection_ReturnsPpdsDbConnection()
    {
        var conn = PpdsDbProviderFactory.Instance.CreateConnection();
        conn.Should().BeOfType<PpdsDbConnection>();
    }

    [Fact]
    public void CreateCommand_ReturnsPpdsDbCommand()
    {
        var cmd = PpdsDbProviderFactory.Instance.CreateCommand();
        cmd.Should().BeOfType<PpdsDbCommand>();
    }

    [Fact]
    public void CreateParameter_ReturnsPpdsDbParameter()
    {
        var param = PpdsDbProviderFactory.Instance.CreateParameter();
        param.Should().BeOfType<PpdsDbParameter>();
    }

    [Fact]
    public void CreateConnectionStringBuilder_ReturnsPpdsConnectionStringBuilder()
    {
        var csb = PpdsDbProviderFactory.Instance.CreateConnectionStringBuilder();
        csb.Should().BeOfType<PpdsConnectionStringBuilder>();
    }

    [Fact]
    public void CanCreateDataSourceEnumerator_ReturnsFalse()
    {
        PpdsDbProviderFactory.Instance.CanCreateDataSourceEnumerator.Should().BeFalse();
    }
}

using FluentAssertions;
using PPDS.Query.Provider;
using Xunit;

namespace PPDS.Query.Tests.Provider;

[Trait("Category", "Unit")]
public class PpdsConnectionStringBuilderTests
{
    // ────────────────────────────────────────────
    //  Parse connection string
    // ────────────────────────────────────────────

    [Fact]
    public void Parse_FullConnectionString_ExtractsAllProperties()
    {
        var builder = new PpdsConnectionStringBuilder(
            "Url=https://org.crm.dynamics.com;AuthType=ClientSecret;ClientId=abc;ClientSecret=secret;TenantId=tid");

        builder.Url.Should().Be("https://org.crm.dynamics.com");
        builder.AuthType.Should().Be("ClientSecret");
        builder.ClientId.Should().Be("abc");
        builder.ClientSecret.Should().Be("secret");
        builder.TenantId.Should().Be("tid");
    }

    [Fact]
    public void Parse_PartialConnectionString_ReturnsEmptyForMissing()
    {
        var builder = new PpdsConnectionStringBuilder("Url=https://org.crm.dynamics.com");

        builder.Url.Should().Be("https://org.crm.dynamics.com");
        builder.AuthType.Should().BeEmpty();
        builder.ClientId.Should().BeEmpty();
        builder.ClientSecret.Should().BeEmpty();
        builder.TenantId.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyConnectionString_ReturnsDefaults()
    {
        var builder = new PpdsConnectionStringBuilder("");

        builder.Url.Should().BeEmpty();
        builder.AuthType.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Build connection string
    // ────────────────────────────────────────────

    [Fact]
    public void Build_SetProperties_ProducesValidConnectionString()
    {
        var builder = new PpdsConnectionStringBuilder
        {
            Url = "https://test.crm.dynamics.com",
            AuthType = "OAuth",
            ClientId = "my-client-id"
        };

        builder.ConnectionString.Should().Contain("Url=https://test.crm.dynamics.com");
        builder.ConnectionString.Should().Contain("AuthType=OAuth");
        builder.ConnectionString.Should().Contain("ClientId=my-client-id");
    }

    [Fact]
    public void Build_SetThenGet_Roundtrips()
    {
        var builder = new PpdsConnectionStringBuilder
        {
            Url = "https://org.crm.dynamics.com",
            AuthType = "ClientSecret",
            ClientId = "cid",
            ClientSecret = "csec",
            TenantId = "tid"
        };

        var cs = builder.ConnectionString;

        var parsed = new PpdsConnectionStringBuilder(cs);
        parsed.Url.Should().Be("https://org.crm.dynamics.com");
        parsed.AuthType.Should().Be("ClientSecret");
        parsed.ClientId.Should().Be("cid");
        parsed.ClientSecret.Should().Be("csec");
        parsed.TenantId.Should().Be("tid");
    }

    // ────────────────────────────────────────────
    //  Default constructor
    // ────────────────────────────────────────────

    [Fact]
    public void DefaultConstructor_CreatesEmpty()
    {
        var builder = new PpdsConnectionStringBuilder();

        builder.Url.Should().BeEmpty();
        builder.ConnectionString.Should().BeEmpty();
    }

    // ────────────────────────────────────────────
    //  Property modification
    // ────────────────────────────────────────────

    [Fact]
    public void SetUrl_UpdatesConnectionString()
    {
        var builder = new PpdsConnectionStringBuilder();
        builder.Url = "https://new.crm.dynamics.com";

        builder.ConnectionString.Should().Contain("Url=https://new.crm.dynamics.com");
    }

    [Fact]
    public void OverwriteProperty_UpdatesValue()
    {
        var builder = new PpdsConnectionStringBuilder("Url=https://old.crm.dynamics.com");
        builder.Url = "https://new.crm.dynamics.com";

        builder.Url.Should().Be("https://new.crm.dynamics.com");
    }
}

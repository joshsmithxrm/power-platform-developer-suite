using FluentAssertions;
using Microsoft.Identity.Client;
using PPDS.Auth.Cloud;
using Xunit;

namespace PPDS.Auth.Tests.Cloud;

public class CloudEndpointsTests
{
    [Theory]
    [InlineData(CloudEnvironment.Public, "https://login.microsoftonline.com/organizations")]
    [InlineData(CloudEnvironment.UsGov, "https://login.microsoftonline.us/organizations")]
    [InlineData(CloudEnvironment.UsGovHigh, "https://login.microsoftonline.us/organizations")]
    [InlineData(CloudEnvironment.UsGovDod, "https://login.microsoftonline.us/organizations")]
    [InlineData(CloudEnvironment.China, "https://login.chinacloudapi.cn/organizations")]
    public void GetAuthorityUrl_WithoutTenant_ReturnsOrganizations(CloudEnvironment cloud, string expected)
    {
        var result = CloudEndpoints.GetAuthorityUrl(cloud);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(CloudEnvironment.Public, "tenant-id", "https://login.microsoftonline.com/tenant-id")]
    [InlineData(CloudEnvironment.UsGov, "tenant-id", "https://login.microsoftonline.us/tenant-id")]
    [InlineData(CloudEnvironment.China, "tenant-id", "https://login.chinacloudapi.cn/tenant-id")]
    public void GetAuthorityUrl_WithTenant_ReturnsTenantUrl(CloudEnvironment cloud, string tenantId, string expected)
    {
        var result = CloudEndpoints.GetAuthorityUrl(cloud, tenantId);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(CloudEnvironment.Public, "https://login.microsoftonline.com")]
    [InlineData(CloudEnvironment.UsGov, "https://login.microsoftonline.us")]
    [InlineData(CloudEnvironment.UsGovHigh, "https://login.microsoftonline.us")]
    [InlineData(CloudEnvironment.UsGovDod, "https://login.microsoftonline.us")]
    [InlineData(CloudEnvironment.China, "https://login.chinacloudapi.cn")]
    public void GetAuthorityBaseUrl_ReturnsCorrectUrl(CloudEnvironment cloud, string expected)
    {
        var result = CloudEndpoints.GetAuthorityBaseUrl(cloud);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(CloudEnvironment.Public, AzureCloudInstance.AzurePublic)]
    [InlineData(CloudEnvironment.UsGov, AzureCloudInstance.AzureUsGovernment)]
    [InlineData(CloudEnvironment.UsGovHigh, AzureCloudInstance.AzureUsGovernment)]
    [InlineData(CloudEnvironment.UsGovDod, AzureCloudInstance.AzureUsGovernment)]
    [InlineData(CloudEnvironment.China, AzureCloudInstance.AzureChina)]
    public void GetAzureCloudInstance_ReturnsCorrectInstance(CloudEnvironment cloud, AzureCloudInstance expected)
    {
        var result = CloudEndpoints.GetAzureCloudInstance(cloud);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(CloudEnvironment.Public, "https://globaldisco.crm.dynamics.com")]
    [InlineData(CloudEnvironment.UsGov, "https://globaldisco.crm9.dynamics.com")]
    [InlineData(CloudEnvironment.UsGovHigh, "https://globaldisco.crm.microsoftdynamics.us")]
    [InlineData(CloudEnvironment.UsGovDod, "https://globaldisco.crm.appsplatform.us")]
    [InlineData(CloudEnvironment.China, "https://globaldisco.crm.dynamics.cn")]
    public void GetGlobalDiscoveryUrl_ReturnsCorrectUrl(CloudEnvironment cloud, string expected)
    {
        var result = CloudEndpoints.GetGlobalDiscoveryUrl(cloud);

        result.Should().Be(expected);
    }

    [Fact]
    public void GetAuthorityHost_Public_ReturnsPublicCloud()
    {
        var result = CloudEndpoints.GetAuthorityHost(CloudEnvironment.Public);

        result.Should().Be(new Uri("https://login.microsoftonline.com"));
    }

    [Fact]
    public void GetAuthorityHost_UsGov_ReturnsGovernmentCloud()
    {
        var result = CloudEndpoints.GetAuthorityHost(CloudEnvironment.UsGov);

        result.Should().Be(new Uri("https://login.microsoftonline.us"));
    }

    [Fact]
    public void GetAuthorityHost_China_ReturnsChinaCloud()
    {
        var result = CloudEndpoints.GetAuthorityHost(CloudEnvironment.China);

        // Azure.Identity's AzureAuthorityHosts.AzureChina uses login.chinacloudapi.cn
        result.Should().Be(new Uri("https://login.chinacloudapi.cn"));
    }

    [Theory]
    [InlineData("PUBLIC", CloudEnvironment.Public)]
    [InlineData("public", CloudEnvironment.Public)]
    [InlineData("Public", CloudEnvironment.Public)]
    [InlineData("USGOV", CloudEnvironment.UsGov)]
    [InlineData("usgov", CloudEnvironment.UsGov)]
    [InlineData("USGOVHIGH", CloudEnvironment.UsGovHigh)]
    [InlineData("USGOVDOD", CloudEnvironment.UsGovDod)]
    [InlineData("CHINA", CloudEnvironment.China)]
    public void Parse_ValidValue_ReturnsCorrectCloud(string value, CloudEnvironment expected)
    {
        var result = CloudEndpoints.Parse(value);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Parse_NullOrWhitespace_ReturnsPublic(string? value)
    {
        var result = CloudEndpoints.Parse(value!);

        result.Should().Be(CloudEnvironment.Public);
    }

    [Fact]
    public void Parse_InvalidValue_Throws()
    {
        var act = () => CloudEndpoints.Parse("invalid");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown cloud environment*");
    }

    [Fact]
    public void GetAuthorityBaseUrl_InvalidCloud_Throws()
    {
        var act = () => CloudEndpoints.GetAuthorityBaseUrl((CloudEnvironment)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetAzureCloudInstance_InvalidCloud_Throws()
    {
        var act = () => CloudEndpoints.GetAzureCloudInstance((CloudEnvironment)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetGlobalDiscoveryUrl_InvalidCloud_Throws()
    {
        var act = () => CloudEndpoints.GetGlobalDiscoveryUrl((CloudEnvironment)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetAuthorityHost_InvalidCloud_Throws()
    {
        var act = () => CloudEndpoints.GetAuthorityHost((CloudEnvironment)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(CloudEnvironment.Public, "https://api.powerapps.com")]
    [InlineData(CloudEnvironment.UsGov, "https://gov.api.powerapps.us")]
    [InlineData(CloudEnvironment.UsGovHigh, "https://high.api.powerapps.us")]
    [InlineData(CloudEnvironment.UsGovDod, "https://api.apps.appsplatform.us")]
    [InlineData(CloudEnvironment.China, "https://api.powerapps.cn")]
    public void GetPowerAppsApiUrl_ReturnsCorrectUrl(CloudEnvironment cloud, string expected)
    {
        var result = CloudEndpoints.GetPowerAppsApiUrl(cloud);

        result.Should().Be(expected);
    }

    [Fact]
    public void GetPowerAppsApiUrl_InvalidCloud_Throws()
    {
        var act = () => CloudEndpoints.GetPowerAppsApiUrl((CloudEnvironment)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(CloudEnvironment.Public, "https://api.flow.microsoft.com")]
    [InlineData(CloudEnvironment.UsGov, "https://gov.api.flow.microsoft.us")]
    [InlineData(CloudEnvironment.UsGovHigh, "https://high.api.flow.microsoft.us")]
    [InlineData(CloudEnvironment.UsGovDod, "https://api.flow.appsplatform.us")]
    [InlineData(CloudEnvironment.China, "https://api.flow.microsoft.cn")]
    public void GetPowerAutomateApiUrl_ReturnsCorrectUrl(CloudEnvironment cloud, string expected)
    {
        var result = CloudEndpoints.GetPowerAutomateApiUrl(cloud);

        result.Should().Be(expected);
    }

    [Fact]
    public void GetPowerAutomateApiUrl_InvalidCloud_Throws()
    {
        var act = () => CloudEndpoints.GetPowerAutomateApiUrl((CloudEnvironment)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

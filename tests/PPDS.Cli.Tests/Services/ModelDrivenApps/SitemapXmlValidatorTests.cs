using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.ModelDrivenApps;
using Xunit;

namespace PPDS.Cli.Tests.Services.ModelDrivenApps;

public class SitemapXmlValidatorTests
{
    private readonly SitemapXmlValidator _validator = new(new SitemapSchemaResources());

    private static XDocument ValidSiteMap() => XDocument.Parse(@"
<SiteMap>
  <Area Id=""Area1"" Title=""My Area"">
    <Group Id=""Group1"" Title=""My Group"">
      <SubArea Id=""Sub1"" Entity=""account"" Title=""Accounts"" />
    </Group>
  </Area>
</SiteMap>");

    [Fact]
    public void Validate_ValidSiteMap_DoesNotThrow()
    {
        var doc = ValidSiteMap();

        var ex = Record.Exception(() => _validator.Validate(doc));

        Assert.Null(ex);
    }

    [Fact]
    public void Validate_InvalidElement_ThrowsPpdsValidationException()
    {
        var doc = XDocument.Parse(@"<SiteMap><InvalidElement /></SiteMap>");

        var ex = Assert.Throws<PpdsValidationException>(() => _validator.Validate(doc));

        Assert.Equal(ModelDrivenAppErrorCodes.InvalidSitemapXml, ex.ErrorCode);
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void Validate_InvalidAttribute_ThrowsWithElementInfo()
    {
        var doc = XDocument.Parse(@"
<SiteMap>
  <Area Id=""Area1"" InvalidAttr=""bad"">
    <Group Id=""Group1"">
      <SubArea Id=""Sub1"" Entity=""account"" />
    </Group>
  </Area>
</SiteMap>");

        var ex = Assert.Throws<PpdsValidationException>(() => _validator.Validate(doc));

        Assert.Equal(ModelDrivenAppErrorCodes.InvalidSitemapXml, ex.ErrorCode);
        Assert.Contains(ex.Errors, e => e.Message.Contains("InvalidAttr", StringComparison.OrdinalIgnoreCase) ||
                                        e.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptySiteMap_IsValid()
    {
        var doc = XDocument.Parse("<SiteMap />");

        var ex = Record.Exception(() => _validator.Validate(doc));

        Assert.Null(ex);
    }
}

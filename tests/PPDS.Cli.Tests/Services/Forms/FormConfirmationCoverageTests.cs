using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

[Trait("Category", "Unit")]
public sealed class FormConfirmationCoverageTests
{
    [Fact]
    public async Task EveryExistingWriter_FailsClosedWithoutContext_BeforeClientAcquisition()
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        var service = new FormService(pool.Object, Mock.Of<IMetadataQueryService>(), NullLogger<FormService>.Instance);
        Func<Task>[] writes =
        [
            () => service.SetFormXmlAsync(new("account", "form", "<form/>")),
            () => service.AddTabAsync(new("account", "form", "tab")),
            () => service.UpdateTabAsync(new("account", "form", "tab")),
            () => service.RemoveTabAsync(new("account", "form", "tab")),
            () => service.AddSectionAsync(new("account", "form", "tab", "section")),
            () => service.UpdateSectionAsync(new("account", "form", "section")),
            () => service.RemoveSectionAsync(new("account", "form", "section")),
            () => service.AddFieldAsync(new("account", "form", "section", ["name"])),
            () => service.RemoveFieldAsync(new("account", "form", "name")),
            () => service.ReorderFieldsAsync(new("account", "form", "section", ["name"])),
            () => service.AddSubgridAsync(new("account", "form", "section", "grid", "contact", Guid.NewGuid())),
            () => service.RemoveSubgridAsync(new("account", "form", "grid"))
        ];
        foreach (var write in writes)
            (await write.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be("Environment.TargetUnresolved");
        pool.VerifyNoOtherCalls();
    }
}

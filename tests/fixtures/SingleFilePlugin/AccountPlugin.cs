using System;
using Microsoft.Xrm.Sdk;
using PPDS.Plugins;

namespace SingleFileFixture;

[PluginStep(Message = "Update", EntityLogicalName = "account", Stage = PluginStage.PreOperation,
    FilteringAttributes = "name,telephone1", ExecutionOrder = 7)]
[PluginImage(ImageType = PluginImageType.PreImage, Name = "Before", Attributes = "name,telephone1")]
public sealed class AccountPlugin : IPlugin
{
    // Extraction must inspect metadata without instantiating or executing the fixture.
    public AccountPlugin() => throw new InvalidOperationException("Fixture must never execute.");

    public void Execute(IServiceProvider serviceProvider)
        => throw new InvalidOperationException("Fixture must never execute.");
}

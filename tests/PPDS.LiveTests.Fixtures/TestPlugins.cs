using System;
using Microsoft.Xrm.Sdk;
using PPDS.Plugins;

namespace PPDS.LiveTests.Fixtures
{
    /// <summary>
    /// Test plugin for account create - synchronous pre-operation.
    /// Does nothing, just validates registration works.
    /// </summary>
    [PluginStep("Create", "account", PluginStage.PreOperation)]
    public class TestAccountCreatePlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            // No-op plugin for testing registration
        }
    }

    /// <summary>
    /// Test plugin for contact update - async post-operation with pre-image.
    /// Does nothing, just validates registration with images works.
    /// </summary>
    [PluginStep("Update", "contact", PluginStage.PostOperation, Mode = PluginMode.Asynchronous)]
    [PluginImage(PluginImageType.PreImage, "PreImage", "firstname,lastname")]
    public class TestContactUpdatePlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            // No-op plugin for testing registration with images
        }
    }
}

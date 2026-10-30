using System;
using Microsoft.Xrm.Sdk;
using PPDS.Plugins;
namespace Ppds1450Smoke;
[PluginStep(Message = "Create", EntityLogicalName = "account", Stage = PluginStage.PreOperation)]
public sealed class MarkerPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        if (context.MessageName != "Create" || context.PrimaryEntityName != "account"
            || !context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
            return;
        var name = target.GetAttributeValue<string>("name");
        if (name == null || !name.StartsWith("PPDS1450_61e24a05_", StringComparison.Ordinal))
            return;
#if SMOKE_V2
        target["description"] = "PPDS1450_61e24a05_v2";
#else
        target["description"] = "PPDS1450_61e24a05_v1";
#endif
    }
}

using System.Linq;
using PPDS.Cli.Infrastructure.Safety;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure.Safety;

/// <summary>
/// AC-01: <see cref="IShakedownGuard"/> exposes exactly one public method —
/// <see cref="IShakedownGuard.EnsureCanMutate"/> — taking a single
/// <see cref="string"/>. Reflection keeps this guarantee enforced even if
/// someone later adds an accidental second method.
/// </summary>
public class ShakedownGuardInterfaceTests
{
    [Fact]
    public void Interface_Has_OnlyEnsureCanMutate()
    {
        var iface = typeof(IShakedownGuard);
        Assert.Equal("PPDS.Cli.Infrastructure.Safety", iface.Namespace);

        var methods = iface.GetMethods();
        Assert.Single(methods);

        var method = methods[0];
        Assert.Equal("EnsureCanMutate", method.Name);
        Assert.Equal(typeof(void), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
    }
}

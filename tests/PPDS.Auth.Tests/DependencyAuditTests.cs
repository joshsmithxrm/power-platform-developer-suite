using System;
using System.Linq;
using FluentAssertions;
using PPDS.Auth.Credentials;
using Xunit;

namespace PPDS.Auth.Tests;

public sealed class DependencyAuditTests
{
    [Fact]
    public void PpdsAuthAssembly_DoesNotReferenceDevlooped()
    {
        var referenced = typeof(NativeCredentialStore).Assembly.GetReferencedAssemblies();
        referenced.Should().NotContain(
            a => a.Name != null && a.Name.StartsWith("Devlooped", StringComparison.OrdinalIgnoreCase),
            because: "PPDS.Auth must not reference any Devlooped.* assembly after vendoring git-credential-manager (see spec AC-13)");
    }
}

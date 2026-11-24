using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Sdk;

[assembly: PPDS.Tests.Shared.InteractiveSignInTripwire]

namespace PPDS.Tests.Shared;

/// <summary>
/// Fails a test when PPDS.Auth or PPDS.Dataverse blocked an interactive sign-in (#1757).
/// </summary>
/// <remarks>
/// <para>
/// tests/Directory.Build.props compiles this file into every test project and turns on the
/// <c>PPDS.BlockInteractiveSignIn</c> AppContext switch in its runtime configuration. With the switch on,
/// the libraries' guard throws before any browser, device-code or OAuth-prompt sign-in, and adds the
/// message to the queue this class stores in AppContext data. Product code may catch that exception (for
/// example, environment resolution turns it into a failed result), so the exception alone would not
/// fail the test. After every test this tripwire takes everything from the queue and fails the test if
/// anything was there.
/// </para>
/// <para>
/// The queue is process-wide, so with tests running in parallel the failure lands on whichever test
/// finishes next: the message names the test it failed, which is the culprit or one that ran at the
/// same time. An attempt made outside any test (in a fixture) fails the next test to finish. An attempt
/// during the disposal of the last fixture of a run is not reported.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InteractiveSignInTripwireAttribute : BeforeAfterTestAttribute
{
    /// <summary>The AppContext switch that blocks interactive sign-in (see the libraries' InteractiveSignInGuard).</summary>
    public const string SwitchName = "PPDS.BlockInteractiveSignIn";

    /// <summary>The AppContext data name the libraries' guard adds blocked attempts to.</summary>
    public const string BlockedAttemptsDataName = "PPDS.BlockedInteractiveSignIns";

    /// <summary>Attempts the guard blocked and no test has been failed for yet.</summary>
    public static ConcurrentQueue<string> BlockedAttempts { get; } = new();

    /// <summary>Tests this tripwire has wrapped, so a project can check that xunit applies it.</summary>
    public static ConcurrentDictionary<string, bool> WrappedTests { get; } = new();

    // Installed when the test assembly loads, before any fixture or test can reach the guard.
#pragma warning disable CA2255 // ModuleInitializer in a test assembly is intended here.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void InstallQueue() => AppContext.SetData(BlockedAttemptsDataName, BlockedAttempts);

    /// <inheritdoc />
    public override void Before(MethodInfo methodUnderTest)
    {
        WrappedTests[Name(methodUnderTest)] = true;
    }

    /// <inheritdoc />
    public override void After(MethodInfo methodUnderTest)
    {
        var attempts = new List<string>();
        while (BlockedAttempts.TryDequeue(out var attempt))
        {
            attempts.Add(attempt);
        }

        if (attempts.Count > 0)
        {
            throw new XunitException(
                $"{attempts.Count} interactive sign-in attempt(s) were blocked while {Name(methodUnderTest)} ran " +
                "(this test, or one running at the same time, reached a real sign-in; fake the credential, " +
                "discovery service or connection instead):" + Environment.NewLine + string.Join(Environment.NewLine, attempts));
        }
    }

    internal static string Name(MethodInfo method) => $"{method.DeclaringType?.FullName}.{method.Name}";
}

/// <summary>
/// Compiled into every test project: the switch is on and the tripwire wraps the tests here.
/// </summary>
public sealed class InteractiveSignInTripwireSelfTests
{
    [Fact]
    public void InteractiveSignInIsBlockedInThisTestProcess()
    {
        Assert.True(
            AppContext.TryGetSwitch(InteractiveSignInTripwireAttribute.SwitchName, out var blocked) && blocked,
            $"tests/Directory.Build.props must set the AppContext switch {InteractiveSignInTripwireAttribute.SwitchName} for this test project.");
    }

    [Fact]
    public void TheTripwireWrapsTestsInThisAssembly()
    {
        var self = typeof(InteractiveSignInTripwireSelfTests).GetMethod(nameof(TheTripwireWrapsTestsInThisAssembly))!;
        Assert.True(
            InteractiveSignInTripwireAttribute.WrappedTests.ContainsKey(InteractiveSignInTripwireAttribute.Name(self)),
            "xunit did not run the assembly-level InteractiveSignInTripwire before this test.");
        Assert.Same(
            InteractiveSignInTripwireAttribute.BlockedAttempts,
            AppContext.GetData(InteractiveSignInTripwireAttribute.BlockedAttemptsDataName));
    }
}

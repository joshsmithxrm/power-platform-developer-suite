using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Internal;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests that reach the guard on purpose. They take their own attempts off the tripwire's queue,
/// so they run alone: a test finishing at the same time would otherwise be failed for them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InteractiveSignInGuardCollection
{
    public const string Name = "Interactive sign-in guard (reaches the guard on purpose)";
}

[Collection(InteractiveSignInGuardCollection.Name)]
public class InteractiveSignInGuardTests
{
    [Fact]
    public void TheGuardIsOnInThisTestRun()
    {
        InteractiveSignInGuard.IsBlocked.Should().BeTrue();
        InteractiveSignInGuard.SwitchName.Should().Be(InteractiveSignInTripwireAttribute.SwitchName);
        InteractiveSignInGuard.BlockedAttemptsDataName.Should().Be(InteractiveSignInTripwireAttribute.BlockedAttemptsDataName);
    }

    [Theory]
    [InlineData("browser")]
    [InlineData("device code")]
    public void ThrowIfBlocked_Throws_AndRecordsTheAttemptForTheTripwire(string flow)
    {
        var act = () => InteractiveSignInGuard.ThrowIfBlocked(flow);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain($"({flow})").And.Contain(InteractiveSignInGuard.SwitchName);
        TakeRecordedAttempts().Should().Equal(message);
    }

    [Theory]
    [InlineData("AuthType=OAuth;Url=https://org.example.test;ClientId=c;RedirectUri=http://localhost;LoginPrompt=Auto")]
    [InlineData("authtype = oauth ; url=https://org.example.test")]
    [InlineData("AuthenticationType=OAuth;Url=https://org.example.test")]
    [InlineData("AuthType=5;Url=https://org.example.test;ClientId=c;RedirectUri=http://localhost")]
    [InlineData("AuthType= 05;Url=https://org.example.test")]
    [InlineData("AuthType=+5;Url=https://org.example.test")]
    [InlineData("AuthenticationType=5;Url=https://org.example.test")]
    [InlineData("AuthType=\"OAuth\";Url=https://org.example.test")]
    public void ThrowIfInteractiveConnectionString_BlocksOAuth(string connectionString)
    {
        var act = () => InteractiveSignInGuard.ThrowIfInteractiveConnectionString(connectionString);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("(OAuth connection string)");
        TakeRecordedAttempts().Should().HaveCount(1);
    }

    [Theory]
    [InlineData("AuthType=ClientSecret;Url=https://org.example.test;ClientId=c;ClientSecret=s")]
    [InlineData("AuthType=Certificate;Url=https://org.example.test;ClientId=c;Thumbprint=t")]
    [InlineData("AuthType=ClientSecret;AuthenticationType=OAuth;Url=https://org.example.test")]
    [InlineData("AuthType=7;Url=https://org.example.test")]
    [InlineData("AuthType=6;Url=https://org.example.test")]
    [InlineData("AuthType=OAuth,ClientSecret;Url=https://org.example.test")]
    [InlineData("AuthType=ClientSecret;Url=https://oauth.example.test;ClientId=c;ClientSecret=OAuth")]
    [InlineData("")]
    [InlineData(null)]
    public void ThrowIfInteractiveConnectionString_AllowsNonInteractiveAuth(string? connectionString)
    {
        var act = () => InteractiveSignInGuard.ThrowIfInteractiveConnectionString(connectionString);

        act.Should().NotThrow();
        TakeRecordedAttempts().Should().BeEmpty();
    }

    [Fact]
    public void WithTheSwitchOff_NeitherGuardRefusesAnything()
    {
        // What every user gets: the switch is absent from the product's runtime configuration.
        // This collection runs alone, so turning the switch off here affects no other test.
        AppContext.SetSwitch(InteractiveSignInGuard.SwitchName, false);
        try
        {
            InteractiveSignInGuard.IsBlocked.Should().BeFalse();
            var calls = new Action[]
            {
                () => InteractiveSignInGuard.ThrowIfBlocked("browser"),
                () => InteractiveSignInGuard.ThrowIfBlocked("device code"),
                () => InteractiveSignInGuard.ThrowIfInteractiveConnectionString(
                    "AuthType=OAuth;Url=https://org.example.test;ClientId=c;RedirectUri=http://localhost;LoginPrompt=Auto"),
                () => InteractiveSignInGuard.ThrowIfInteractiveConnectionString("AuthType=5;Url=https://org.example.test"),
                () => InteractiveSignInGuard.ThrowIfInteractiveConnectionString("not=a;;valid==oauth string'"),
                () => InteractiveSignInGuard.ThrowIfInteractiveConnectionString(null),
            };

            foreach (var call in calls)
            {
                call.Should().NotThrow();
            }

            TakeRecordedAttempts().Should().BeEmpty();
        }
        finally
        {
            AppContext.SetSwitch(InteractiveSignInGuard.SwitchName, true);
        }

        InteractiveSignInGuard.IsBlocked.Should().BeTrue();
    }

    [Theory]
    [InlineData("AuthType=OAuth")]
    [InlineData("AuthType=5")]
    [InlineData("AuthType= 5")]
    [InlineData("AuthType=05")]
    [InlineData("AuthType=+5")]
    [InlineData("AuthType = OAuth")]
    [InlineData("AuthType=\"OAuth\"")]
    [InlineData("AuthType='OAuth'")]
    [InlineData("AuthenticationType=OAuth")]
    [InlineData("AuthenticationType=5")]
    [InlineData("AuthType=ClientSecret;AuthenticationType=OAuth")]
    [InlineData("AuthenticationType=OAuth;AuthType=ClientSecret")]
    [InlineData("AuthType=;AuthenticationType=OAuth")]
    [InlineData("AuthType=OAuth,ClientSecret")]
    [InlineData("AuthType=ClientSecret")]
    [InlineData("AuthType=7")]
    [InlineData("AuthType=Certificate")]
    [InlineData("AuthType=6")]
    [InlineData("AuthType=AD")]
    [InlineData("AuthType=0")]
    [InlineData("AuthType=99")]
    [InlineData("AuthType=5.0")]
    [InlineData("AuthType=0x5")]
    [InlineData("AuthType=-1")]
    public void UsesOAuth_AgreesWithTheDataverseSdk(string authPart)
    {
        // Ask the SDK's own connection-string parser (internal, so by reflection) how it reads the string.
        var connectionString = authPart + ";Url=https://org.example.test;ClientId=c;RedirectUri=http://localhost";
        var sdk = SdkAuthenticationType(connectionString);

        if (sdk == Microsoft.PowerPlatform.Dataverse.Client.AuthenticationType.OAuth)
        {
            InteractiveSignInGuard.UsesOAuth(connectionString).Should().BeTrue(because: "the SDK reads it as OAuth");
        }
        else if (sdk != Microsoft.PowerPlatform.Dataverse.Client.AuthenticationType.InvalidConnection)
        {
            InteractiveSignInGuard.UsesOAuth(connectionString).Should().BeFalse(because: $"the SDK reads it as {sdk}");
        }
    }

    [Fact]
    public void TheTripwire_FailsTheTest_WhenAnAttemptIsQueued()
    {
        var method = typeof(InteractiveSignInGuardTests).GetMethod(nameof(TheTripwire_FailsTheTest_WhenAnAttemptIsQueued))!;
        InteractiveSignInTripwireAttribute.BlockedAttempts.Enqueue("probe: a blocked attempt");

        var act = () => new InteractiveSignInTripwireAttribute().After(method);

        act.Should().Throw<Xunit.Sdk.XunitException>()
            .Which.Message.Should().Contain("probe: a blocked attempt")
            .And.Contain(nameof(TheTripwire_FailsTheTest_WhenAnAttemptIsQueued));
        TakeRecordedAttempts().Should().BeEmpty(because: "the tripwire reports each attempt once");
    }

    [Fact]
    public void TheTripwire_PassesTheTest_WhenNothingIsQueued()
    {
        var method = typeof(InteractiveSignInGuardTests).GetMethod(nameof(TheTripwire_PassesTheTest_WhenNothingIsQueued))!;
        TakeRecordedAttempts().Should().BeEmpty();

        var act = () => new InteractiveSignInTripwireAttribute().After(method);

        act.Should().NotThrow();
    }

    private static Microsoft.PowerPlatform.Dataverse.Client.AuthenticationType SdkAuthenticationType(string connectionString)
    {
        var processor = typeof(Microsoft.PowerPlatform.Dataverse.Client.ServiceClient).Assembly.GetTypes()
            .Single(t => t.Name == "DataverseConnectionStringProcessor");
        var parse = processor.GetMethod("Parse", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("The Dataverse SDK's DataverseConnectionStringProcessor.Parse was not found; update this test for the SDK version.");
        var parsed = parse.Invoke(null, new object?[] { connectionString, null });
        return (Microsoft.PowerPlatform.Dataverse.Client.AuthenticationType)processor
            .GetProperty("AuthenticationType", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(parsed)!;
    }

    [Fact]
    public async Task AnAttemptTheProductCatches_IsStillRecordedForTheTripwire()
    {
        // Environment resolution turns any discovery failure into a failed result, so the guard's
        // exception never reaches the test. The tripwire still sees the attempt; this test takes it
        // off the queue itself, which an accidental test would not do, so the tripwire would fail that test.
        var profile = new AuthProfile { AuthMethod = AuthMethod.InteractiveBrowser };
        using var service = new EnvironmentResolutionService(
            profile,
            deviceCodeCallback: null,
            credentialStore: null,
            createGlobalDiscovery: _ => new SignInReachingDiscovery(),
            createBapDiscovery: (_, _, _) => throw new InvalidOperationException("not expected"));

        var result = await service.ResolveAsync("MyEnvironment");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Global Discovery failed").And.Contain("(browser)");
        TakeRecordedAttempts().Should().HaveCount(1);
    }

    private static List<string> TakeRecordedAttempts()
    {
        var attempts = new List<string>();
        while (InteractiveSignInTripwireAttribute.BlockedAttempts.TryDequeue(out var attempt))
        {
            attempts.Add(attempt);
        }

        return attempts;
    }

    private sealed class SignInReachingDiscovery : IEnvironmentDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(CancellationToken cancellationToken = default)
        {
            // Stands in for the real service reaching its browser sign-in.
            InteractiveSignInGuard.ThrowIfBlocked("browser");
            return Task.FromResult<IReadOnlyList<DiscoveredEnvironment>>(Array.Empty<DiscoveredEnvironment>());
        }
    }
}

/// <summary>
/// Reads the product sources (src/) and checks that every interactive sign-in is behind the guard.
/// </summary>
public class InteractiveSignInGuardCoverageTests
{
    private const string Guard = "ThrowIfBlocked";
    private const string ConnectionStringGuard = "ThrowIfInteractiveConnectionString";

    [Fact]
    public void EveryMsalInteractiveAcquisition_IsPrecededByAnUnconditionalGuard()
    {
        var found = 0;
        var offenders = new List<string>();
        foreach (var (path, root) in ProductSources())
        {
            foreach (var call in InteractiveAcquisitions(root))
            {
                found++;
                if (!IsPrecededByGuard(call, Guard))
                {
                    offenders.Add(Location(path, call));
                }
            }
        }

        found.Should().BeGreaterThanOrEqualTo(7, because: "the scan must find the product's interactive flows");
        offenders.Should().BeEmpty(
            because: "an interactive acquisition without an unconditional guard before it can open a browser or wait on a device code inside a test");
    }

    [Fact]
    public void EveryServiceClientConstruction_IsGuardedOrGetsItsTokenFromAProviderFunction()
    {
        // A connection string lets the Dataverse SDK sign in by itself (AuthType=OAuth can prompt), so it
        // must pass the connection-string guard first. The other accepted forms take the token from a
        // function of the caller's (an MSAL flow guarded above, or a non-interactive credential).
        var found = 0;
        var offenders = new List<string>();
        foreach (var (path, root) in ProductSources())
        {
            foreach (var creation in ServiceClientCreations(root))
            {
                found++;
                if (!IsAcceptedServiceClientCreation(creation))
                {
                    offenders.Add(Location(path, creation));
                }
            }
        }

        found.Should().BeGreaterThanOrEqualTo(11);
        offenders.Should().BeEmpty();
    }

    [Fact]
    public void TheProductUsesNoAzureIdentityInteractiveCredential()
    {
        // The guard covers MSAL and SDK connection strings only; an Azure.Identity interactive credential
        // would bypass it and needs its own guard before this test is changed.
        var uses = ProductSources()
            .SelectMany(source => source.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(c => TypeName(c.Type) is "InteractiveBrowserCredential" or "DeviceCodeCredential")
                .Select(c => Location(source.Path, c)))
            .ToList();

        uses.Should().BeEmpty();
    }

    [Theory]
    [InlineData("InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); var r = client.AcquireTokenInteractive(s);", true)]
    [InlineData("InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); try { var r = client.AcquireTokenInteractive(s); } catch { }", true)]
    [InlineData("InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); Func<object> f = () => client.AcquireTokenInteractive(s);", true)]
    [InlineData("// InteractiveSignInGuard.ThrowIfBlocked(\"browser\");\nvar r = client.AcquireTokenInteractive(s);", false)]
    [InlineData("/* InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); */ var r = client.AcquireTokenInteractive(s);", false)]
    [InlineData("if (force) InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); var r = client.AcquireTokenInteractive(s);", false)]
    [InlineData("if (force) { InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); } var r = client.AcquireTokenInteractive(s);", false)]
    [InlineData("try { InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); } catch { } var r = client.AcquireTokenInteractive(s);", false)]
    [InlineData("var r = client.AcquireTokenInteractive(s); InteractiveSignInGuard.ThrowIfBlocked(\"browser\");", false)]
    [InlineData("InteractiveSignInGuard.ThrowIfBlocked(\"browser\"); void Local() { var r = client.AcquireTokenInteractive(s); }", false)]
    [InlineData("var r = client.AcquireTokenInteractive(s);", false)]
    public void TheScan_AcceptsOnlyAGuardThatAlwaysRunsFirst(string body, bool accepted)
    {
        var root = CSharpSyntaxTree.ParseText($"class C {{ void M() {{ {body} }} }}").GetRoot();

        var call = InteractiveAcquisitions(root).Single();

        IsPrecededByGuard(call, Guard).Should().Be(accepted);
    }

    [Theory]
    [InlineData("InteractiveSignInGuard.ThrowIfInteractiveConnectionString(cs); var c = new ServiceClient(cs);", true)]
    [InlineData("var c = new ServiceClient(cs);", false)]
    [InlineData("InteractiveSignInGuard.ThrowIfInteractiveConnectionString(other); var c = new ServiceClient(cs);", false)]
    [InlineData("var options = new ConnectionOptions { ServiceUri = u, AccessTokenProviderFunctionAsync = _ => t }; var c = new ServiceClient(options);", true)]
    [InlineData("var options = new ConnectionOptions { ServiceUri = u, LoginPrompt = p, AccessTokenProviderFunctionAsync = _ => t }; var c = new ServiceClient(options);", false)]
    [InlineData("var options = new ConnectionOptions { ServiceUri = u }; var c = new ServiceClient(options);", false)]
    [InlineData("var c = new ServiceClient(new Uri(u), _ => Task.FromResult(t), useUniqueInstance: true);", true)]
    [InlineData("ServiceClient c = new(cs);", false)]
    [InlineData("InteractiveSignInGuard.ThrowIfInteractiveConnectionString(cs); ServiceClient c = new(cs);", true)]
    [InlineData("ServiceClient? c = new(cs + \";LoginPrompt=Always\");", false)]
    [InlineData("ServiceClient c; c = new(cs);", false)]
    public void TheScan_AcceptsOnlyGuardedOrTokenFunctionServiceClients(string body, bool accepted)
    {
        var root = CSharpSyntaxTree.ParseText($"class C {{ void M() {{ {body} }} }}").GetRoot();

        var creation = ServiceClientCreations(root).Single();

        IsAcceptedServiceClientCreation(creation).Should().Be(accepted);
    }

    [Theory]
    [InlineData("class C { ServiceClient _c; void M() { _c = new(cs); } }")]
    [InlineData("class C { ServiceClient M() { return new(cs); } }")]
    [InlineData("class C { Task<ServiceClient> M() => new(cs); }")]
    [InlineData("class C { ServiceClient P { get; } = new(cs); }")]
    public void TheScan_SeesTargetTypedServiceClients(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        var creation = ServiceClientCreations(root).Single();

        IsAcceptedServiceClientCreation(creation).Should().BeFalse(because: "it has no connection-string guard");
    }

    private static IEnumerable<InvocationExpressionSyntax> InteractiveAcquisitions(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "AcquireTokenInteractive" or "AcquireTokenWithDeviceCode" });

    private static IEnumerable<BaseObjectCreationExpressionSyntax> ServiceClientCreations(SyntaxNode root) =>
        root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().Where(c => c switch
        {
            ObjectCreationExpressionSyntax explicitNew => TypeName(explicitNew.Type) == "ServiceClient",
            ImplicitObjectCreationExpressionSyntax targetTyped => TypeName(TargetType(targetTyped)) == "ServiceClient",
            _ => false,
        });

    /// <summary>
    /// The type a target-typed <c>new(...)</c> creates, when the syntax shows it: a declaration, an
    /// assignment to a local, parameter, field or property declared with a type, or a return value of a
    /// method returning T, Task&lt;T&gt; or ValueTask&lt;T&gt;. Null when only a semantic model could tell
    /// (for example an argument position).
    /// </summary>
    private static TypeSyntax? TargetType(ImplicitObjectCreationExpressionSyntax creation)
    {
        switch (creation.Parent)
        {
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }:
                return declaration.Type;
            case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                return property.Type;
            case AssignmentExpressionSyntax assignment when assignment.Right == creation:
                var name = assignment.Left switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.Text,
                    MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
                    _ => null,
                };
                return name is null ? null : DeclaredType(creation, name);
            case ReturnStatementSyntax:
            case ArrowExpressionClauseSyntax:
                var method = creation.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
                var returnType = method switch
                {
                    MethodDeclarationSyntax m => m.ReturnType,
                    LocalFunctionStatementSyntax f => f.ReturnType,
                    _ => null,
                };
                return returnType is GenericNameSyntax { Identifier.Text: "Task" or "ValueTask", TypeArgumentList.Arguments.Count: 1 } task
                    ? task.TypeArgumentList.Arguments[0]
                    : returnType;
            default:
                return null;
        }
    }

    private static TypeSyntax? DeclaredType(SyntaxNode from, string name)
    {
        var member = from.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not TypeDeclarationSyntax);
        var type = from.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var local = member?.DescendantNodes().OfType<VariableDeclarationSyntax>()
            .FirstOrDefault(d => d.Variables.Any(v => v.Identifier.Text == name))?.Type;
        var parameter = member?.DescendantNodes().OfType<ParameterSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == name)?.Type;
        var field = type?.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == name))?.Declaration.Type;
        var property = type?.Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == name)?.Type;
        return local ?? parameter ?? field ?? property;
    }

    private static bool IsAcceptedServiceClientCreation(BaseObjectCreationExpressionSyntax creation)
    {
        var arguments = creation.ArgumentList?.Arguments ?? default;

        // new ServiceClient(uri, tokenFunction, ...): the caller supplies the token.
        if (arguments.Any(a => a.Expression is LambdaExpressionSyntax))
        {
            return true;
        }

        if (arguments.Count != 1)
        {
            return false;
        }

        var argument = arguments[0].Expression;

        // new ServiceClient(options), where options take the token from the caller and set no prompt.
        if (argument is IdentifierNameSyntax { Identifier.Text: "options" })
        {
            var member = creation.Ancestors().OfType<MemberDeclarationSyntax>().First();
            return member.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(o => TypeName(o.Type) == "ConnectionOptions" && o.Initializer is not null)
                .Any(o =>
                {
                    var assigned = o.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
                        .Select(a => (a.Left as IdentifierNameSyntax)?.Identifier.Text).ToList();
                    return assigned.Contains("AccessTokenProviderFunctionAsync") && !assigned.Contains("LoginPrompt");
                });
        }

        // new ServiceClient(connectionString): the same value must pass the connection-string guard first.
        return IsPrecededByGuard(creation, ConnectionStringGuard, argument.ToString());
    }

    /// <summary>
    /// True when a statement that always runs before <paramref name="node"/> in the same member is
    /// <c>InteractiveSignInGuard.&lt;guard&gt;(...)</c>: an earlier statement of the block that holds the node,
    /// or of a block around it. A guard inside an if, a try or another block does not count, nor
    /// does one outside a local function the node is in.
    /// </summary>
    private static bool IsPrecededByGuard(SyntaxNode node, string guard, string? argument = null)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is MemberDeclarationSyntax or LocalFunctionStatementSyntax)
            {
                return false;
            }

            if (current is StatementSyntax statement)
            {
                var siblings = statement.Parent switch
                {
                    BlockSyntax block => block.Statements,
                    SwitchSectionSyntax section => section.Statements,
                    _ => default(SyntaxList<StatementSyntax>),
                };

                foreach (var sibling in siblings.TakeWhile(s => s != statement))
                {
                    if (IsGuardCall(sibling, guard, argument))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsGuardCall(StatementSyntax statement, string guard, string? argument) =>
        statement is ExpressionStatementSyntax
        {
            Expression: InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.Text: "InteractiveSignInGuard" },
                } access,
            } invocation,
        }
        && access.Name.Identifier.Text == guard
        && (argument is null || invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == argument);

    private static string? TypeName(TypeSyntax? type) => type switch
    {
        IdentifierNameSyntax name => name.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        NullableTypeSyntax nullable => TypeName(nullable.ElementType),
        _ => null,
    };

    private static string SourceRoot
    {
        get
        {
            var baseDir = Path.GetDirectoryName(typeof(InteractiveSignInGuardCoverageTests).Assembly.Location)!;
            return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src"));
        }
    }

    private static IEnumerable<(string Path, SyntaxNode Root)> ProductSources()
    {
        Directory.Exists(SourceRoot).Should().BeTrue(because: "the scan reads the product sources under src/");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{separator}obj{separator}") && !p.Contains($"{separator}bin{separator}")
                && !p.Contains($"{separator}node_modules{separator}"))
            .Select(p => (p, CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p).GetRoot()))
            .ToList();
    }

    private static string Location(string path, SyntaxNode node) =>
        $"{Path.GetRelativePath(SourceRoot, path)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {node}";
}

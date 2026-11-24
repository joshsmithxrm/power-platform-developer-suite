using PPDS.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Security;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Dataverse.Tests.Security;

/// <summary>
/// Tests that reach the interactive sign-in guard on purpose. They take their own attempts off the
/// tripwire's queue, so they run alone: a test finishing at the same time would otherwise be failed for them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InteractiveSignInGuardCollection
{
    public const string Name = "Interactive sign-in guard (reaches the guard on purpose)";
}

/// <summary>
/// An OAuth connection string lets the Dataverse SDK show its own sign-in prompt, so both places this
/// library hands a connection string to the SDK refuse it while the test switch is on (#1757).
/// </summary>
[Collection(InteractiveSignInGuardCollection.Name)]
public class InteractiveSignInGuardTests
{
    [Fact]
    public void AnOAuthConnectionSource_IsBlockedBeforeTheSdkClientIsCreated()
    {
        using var source = new ConnectionStringSource(new DataverseConnection("OAuthSource")
        {
            Url = "https://org.example.test",
            ClientId = "client",
            RedirectUri = "http://localhost",
            AuthType = DataverseAuthType.OAuth,
        });

        var ex = Assert.ThrowsAny<Exception>(() => source.GetSeedClient());

        Assert.Contains("(OAuth connection string)", Flatten(ex));
        Assert.Single(TakeRecordedAttempts());
    }

    [Fact]
    public void AnOAuthDataverseClient_IsBlockedBeforeTheSdkClientIsCreated()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DataverseClient("AuthType=OAuth;Url=https://org.example.test;ClientId=client;RedirectUri=http://localhost;LoginPrompt=Auto"));

        Assert.Contains("(OAuth connection string)", ex.Message);
        Assert.Single(TakeRecordedAttempts());
    }

    private static string Flatten(Exception ex) =>
        ex.InnerException is null ? ex.Message : ex.Message + " | " + Flatten(ex.InnerException);

    private static List<string> TakeRecordedAttempts()
    {
        var attempts = new List<string>();
        while (InteractiveSignInTripwireAttribute.BlockedAttempts.TryDequeue(out var attempt))
        {
            attempts.Add(attempt);
        }

        return attempts;
    }
}

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Verifies that every mutation method on <see cref="DataverseMetadataAuthoringService"/>
/// funnels through <see cref="PPDS.Cli.Infrastructure.Safety.IShakedownGuard"/>.
/// When the guard is active, every method must throw
/// <see cref="PpdsException"/> with <see cref="ErrorCodes.Safety.ShakedownActive"/>.
/// Covers AC-36 of the shakedown-guard spec.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataAuthoringServiceGuardTests
{
    [Theory]
    [InlineData("CreateTableAsync")]
    [InlineData("UpdateTableAsync")]
    [InlineData("DeleteTableAsync")]
    [InlineData("CreateColumnAsync")]
    [InlineData("UpdateColumnAsync")]
    [InlineData("DeleteColumnAsync")]
    [InlineData("CreateOneToManyAsync")]
    [InlineData("CreateManyToManyAsync")]
    [InlineData("UpdateRelationshipAsync")]
    [InlineData("DeleteRelationshipAsync")]
    [InlineData("CreateGlobalChoiceAsync")]
    [InlineData("UpdateGlobalChoiceAsync")]
    [InlineData("DeleteGlobalChoiceAsync")]
    [InlineData("AddOptionValueAsync")]
    [InlineData("UpdateOptionValueAsync")]
    [InlineData("DeleteOptionValueAsync")]
    [InlineData("ReorderOptionsAsync")]
    [InlineData("UpdateStateValueAsync")]
    [InlineData("CreateKeyAsync")]
    [InlineData("DeleteKeyAsync")]
    [InlineData("ReactivateKeyAsync")]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict).Object;
        var validator = new SchemaValidator();
        var guard = new ActiveFakeShakedownGuard();

        var service = new DataverseMetadataAuthoringService(pool, validator, guard);

        Func<Task> act = methodName switch
        {
            "CreateTableAsync" => () => service.CreateTableAsync(new CreateTableRequest()),
            "UpdateTableAsync" => () => service.UpdateTableAsync(new UpdateTableRequest()),
            "DeleteTableAsync" => () => service.DeleteTableAsync(new DeleteTableRequest()),
            "CreateColumnAsync" => () => service.CreateColumnAsync(new CreateColumnRequest()),
            "UpdateColumnAsync" => () => service.UpdateColumnAsync(new UpdateColumnRequest()),
            "DeleteColumnAsync" => () => service.DeleteColumnAsync(new DeleteColumnRequest()),
            "CreateOneToManyAsync" => () => service.CreateOneToManyAsync(new CreateOneToManyRequest()),
            "CreateManyToManyAsync" => () => service.CreateManyToManyAsync(new CreateManyToManyRequest()),
            "UpdateRelationshipAsync" => () => service.UpdateRelationshipAsync(new UpdateRelationshipRequest()),
            "DeleteRelationshipAsync" => () => service.DeleteRelationshipAsync(new DeleteRelationshipRequest()),
            "CreateGlobalChoiceAsync" => () => service.CreateGlobalChoiceAsync(new CreateGlobalChoiceRequest()),
            "UpdateGlobalChoiceAsync" => () => service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest()),
            "DeleteGlobalChoiceAsync" => () => service.DeleteGlobalChoiceAsync(new DeleteGlobalChoiceRequest()),
            "AddOptionValueAsync" => async () => await service.AddOptionValueAsync(new AddOptionValueRequest()),
            "UpdateOptionValueAsync" => () => service.UpdateOptionValueAsync(new UpdateOptionValueRequest()),
            "DeleteOptionValueAsync" => () => service.DeleteOptionValueAsync(new DeleteOptionValueRequest()),
            "ReorderOptionsAsync" => () => service.ReorderOptionsAsync(new ReorderOptionsRequest()),
            "UpdateStateValueAsync" => () => service.UpdateStateValueAsync(new UpdateStateValueRequest()),
            "CreateKeyAsync" => () => service.CreateKeyAsync(new CreateKeyRequest()),
            "DeleteKeyAsync" => () => service.DeleteKeyAsync(new DeleteKeyRequest()),
            "ReactivateKeyAsync" => () => service.ReactivateKeyAsync(new ReactivateKeyRequest()),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown method — update the switch when adding new mutation methods.")
        };

        (await act.Should().ThrowAsync<PpdsException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.Safety.ShakedownActive);
    }
}

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.SolutionComponents;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Solutions;

/// <summary>
/// Verifies that every mutation method on <see cref="SolutionService"/> calls
/// <c>IShakedownGuard.EnsureCanMutate</c> before performing any side effects.
/// </summary>
public class SolutionServiceGuardTests
{
    [Theory]
    [InlineData(nameof(ISolutionService.ImportAsync))]
    [InlineData(nameof(ISolutionService.PublishAllAsync))]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange — guard is active, so every mutation method must throw.
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new ActiveFakeShakedownGuard();
        var logger = new NullLogger<SolutionService>();
        var metadataService = new Mock<IMetadataQueryService>().Object;
        var nameResolver = new Mock<IComponentNameResolver>().Object;
        var cachedMetadata = new Mock<ICachedMetadataProvider>().Object;

        var service = new SolutionService(pool, guard, logger, metadataService, nameResolver, cachedMetadata);

        // Act
        Func<Task> act = () => InvokeMutationAsync(service, methodName);

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Safety.ShakedownActive);
    }

    private static Task InvokeMutationAsync(ISolutionService service, string methodName) => methodName switch
    {
        nameof(ISolutionService.ImportAsync) => service.ImportAsync(new byte[] { 0x00 }),
        nameof(ISolutionService.PublishAllAsync) => service.PublishAllAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown mutation method"),
    };
}

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Roles;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Roles;

/// <summary>
/// Verifies that every mutation method on <see cref="RoleService"/> calls
/// <c>IShakedownGuard.EnsureCanMutate</c> before performing any side effects.
/// </summary>
public class RoleServiceGuardTests
{
    [Theory]
    [InlineData(nameof(IRoleService.AssignRoleAsync))]
    [InlineData(nameof(IRoleService.RemoveRoleAsync))]
    public async Task EveryMutationMethod_Blocks(string methodName)
    {
        // Arrange — guard is active, so every mutation method must throw.
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new ActiveFakeShakedownGuard();
        var logger = new NullLogger<RoleService>();

        var service = new RoleService(pool, guard, logger);
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        // Act
        Func<Task> act = () => InvokeMutationAsync(service, methodName, userId, roleId);

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Safety.ShakedownActive);
    }

    private static Task InvokeMutationAsync(IRoleService service, string methodName, Guid userId, Guid roleId) => methodName switch
    {
        nameof(IRoleService.AssignRoleAsync) => service.AssignRoleAsync(userId, roleId),
        nameof(IRoleService.RemoveRoleAsync) => service.RemoveRoleAsync(userId, roleId),
        _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown mutation method"),
    };
}

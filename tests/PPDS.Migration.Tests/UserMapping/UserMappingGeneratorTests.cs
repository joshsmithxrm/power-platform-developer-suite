using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.UserMapping;
using Xunit;

namespace PPDS.Migration.Tests.UserMapping;

[Trait("Category", "Unit")]
public class UserMappingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_FetchesAllPages_WhenMoreRecordsTrue()
    {
        // F2: QueryUsersAsync must thread PageInfo through and loop until
        // MoreRecords == false. Prior behavior silently truncated directories >5000.
        var sourcePool = CreatePoolReturningPages(new[]
        {
            CreateUserPage(pageSize: 2, moreRecords: true),
            CreateUserPage(pageSize: 2, moreRecords: true),
            CreateUserPage(pageSize: 1, moreRecords: false)
        });

        // Target env has no users — we only care that source paging walked all pages.
        var targetPool = CreatePoolReturningPages(new[]
        {
            CreateUserPage(pageSize: 0, moreRecords: false)
        });

        var sut = new UserMappingGenerator();

        var result = await sut.GenerateAsync(sourcePool.Object, targetPool.Object);

        // Three pages: 2 + 2 + 1 = 5 users total.
        result.SourceUserCount.Should().Be(5,
            "F2: all pages must be retrieved; caller previously truncated at the first page");
    }

    [Fact]
    public async Task GenerateAsync_StopsPaging_WhenMoreRecordsFalse()
    {
        var sourcePool = CreatePoolReturningPages(new[]
        {
            CreateUserPage(pageSize: 3, moreRecords: false)
        });
        var targetPool = CreatePoolReturningPages(new[]
        {
            CreateUserPage(pageSize: 0, moreRecords: false)
        });

        var sut = new UserMappingGenerator();

        var result = await sut.GenerateAsync(sourcePool.Object, targetPool.Object);

        result.SourceUserCount.Should().Be(3);
    }

    [Fact]
    public async Task GenerateAsync_PassesPagingCookieToSubsequentRequests()
    {
        // Paging cookies must round-trip across calls so Dataverse can continue the scan.
        var queries = new List<QueryExpression>();

        var sourcePool = new Mock<IDataverseConnectionPool>();
        var client = new Mock<IPooledClient>();

        var callIndex = 0;
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync((QueryBase q) =>
            {
                var qe = (QueryExpression)q;
                queries.Add(qe);
                callIndex++;

                if (callIndex == 1)
                {
                    return new EntityCollection(new List<Entity>
                    {
                        NewUser(Guid.NewGuid(), "a@example.com")
                    })
                    {
                        MoreRecords = true,
                        PagingCookie = "<cookie page='1'/>"
                    };
                }

                return new EntityCollection(new List<Entity>
                {
                    NewUser(Guid.NewGuid(), "b@example.com")
                })
                { MoreRecords = false };
            });
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        sourcePool.Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);

        var targetPool = CreatePoolReturningPages(new[]
        {
            CreateUserPage(pageSize: 0, moreRecords: false)
        });

        var sut = new UserMappingGenerator();

        var result = await sut.GenerateAsync(sourcePool.Object, targetPool.Object);

        result.SourceUserCount.Should().Be(2);
        queries.Should().HaveCount(2);
        queries[0].PageInfo.PagingCookie.Should().BeNullOrEmpty();
        queries[1].PageInfo.PagingCookie.Should().Be("<cookie page='1'/>",
            "F2: the cookie from page N-1 must be forwarded to page N");
        queries[1].PageInfo.PageNumber.Should().Be(2);
    }

    private static Mock<IDataverseConnectionPool> CreatePoolReturningPages(
        EntityCollection[] pages)
    {
        var pool = new Mock<IDataverseConnectionPool>();
        var client = new Mock<IPooledClient>();

        var index = 0;
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync(() =>
            {
                var p = pages[Math.Min(index, pages.Length - 1)];
                index++;
                return p;
            });
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        pool.Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);

        return pool;
    }

    private static EntityCollection CreateUserPage(int pageSize, bool moreRecords)
    {
        var entities = Enumerable.Range(0, pageSize)
            .Select(i => NewUser(Guid.NewGuid(), $"user{i}@example.com"))
            .ToList();

        return new EntityCollection(entities)
        {
            MoreRecords = moreRecords,
            PagingCookie = moreRecords ? "<cookie/>" : null
        };
    }

    private static Entity NewUser(Guid id, string domainName)
    {
        var e = new Entity(SystemUser.EntityLogicalName, id);
        e[SystemUser.Fields.SystemUserId] = id;
        e[SystemUser.Fields.FullName] = domainName;
        e[SystemUser.Fields.DomainName] = domainName;
        e[SystemUser.Fields.InternalEMailAddress] = domainName;
        e[SystemUser.Fields.IsDisabled] = false;
        return e;
    }
}

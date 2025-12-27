# ADR-0002: Multi-Connection Pooling

**Status:** Accepted
**Date:** 2025-12-22
**Applies to:** PPDS.Dataverse

## Context

Dataverse enforces service protection limits per user:

| Limit | Value | Window |
|-------|-------|--------|
| Number of requests | 6,000 | 5 minutes |
| Execution time | 20 minutes | 5 minutes |
| Concurrent requests | 52 | - |

A single Application User (client credentials) shares these limits across all requests. High-throughput applications quickly exhaust the quota.

## Decision

Support multiple connection configurations, each representing a different Application User:

```csharp
options.Connections = new List<DataverseConnection>
{
    new("AppUser1", connectionString1),
    new("AppUser2", connectionString2),
    new("AppUser3", connectionString3),
};
```

The pool intelligently distributes requests across connections based on the configured selection strategy.

## Consequences

### Positive

- **Multiplied API quota** - N Application Users = N × 6,000 requests per 5 minutes
- **Graceful degradation** - When one user is throttled, others continue serving requests
- **Load balancing** - Distribute work evenly across available quota

### Negative

- Requires provisioning multiple Application Users in Entra ID
- Each Application User needs appropriate security roles in Dataverse
- More complex configuration than single connection

### Configuration Pattern

```csharp
services.AddDataverseConnectionPool(options =>
{
    // Each connection is a separate Application User
    options.Connections.Add(new("Primary", config["Dataverse:Connection1"]));
    options.Connections.Add(new("Secondary", config["Dataverse:Connection2"]));
    options.Connections.Add(new("Tertiary", config["Dataverse:Connection3"]));

    // Automatically avoid throttled connections
    options.Pool.SelectionStrategy = ConnectionSelectionStrategy.ThrottleAware;
});
```

## References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Throttling thresholds per user
- [Application User setup](https://learn.microsoft.com/en-us/power-platform/admin/manage-application-users) - Provisioning app registrations
- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update) - User multiplexing guidance
- [Scaling Dynamics 365 CRM Integrations in Azure](https://techcommunity.microsoft.com/blog/microsoftmissioncriticalblog/scaling-dynamics-365-crm-integrations-in-azure-the-right-way-to-use-the-sdk-s/4447143) - Multi-user distribution patterns

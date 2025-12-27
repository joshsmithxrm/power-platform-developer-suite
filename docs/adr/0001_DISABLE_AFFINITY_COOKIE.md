# ADR-0001: Disable Affinity Cookie by Default

**Status:** Accepted
**Date:** 2025-12-22
**Applies to:** PPDS.Dataverse

## Context

The Dataverse SDK's `ServiceClient` has an `EnableAffinityCookie` property that defaults to `true`. When enabled, an affinity cookie routes all requests from a client instance to the same backend node in Microsoft's load-balanced infrastructure.

This creates a bottleneck: a single backend node handles all requests from your application, regardless of how many connections you create.

## Decision

Disable the affinity cookie by default in PPDS.Dataverse:

```csharp
options.Pool.DisableAffinityCookie = true; // Default
```

When creating ServiceClient instances, we set:

```csharp
serviceClient.EnableAffinityCookie = false;
```

## Consequences

### Positive

- **10x+ throughput improvement** for high-volume operations
- Requests distribute across Microsoft's backend infrastructure
- Better utilization of available server capacity
- Reduced likelihood of hitting per-node limits

### Negative

- Slightly higher latency for individual requests (no connection reuse at the backend)
- May require more careful handling of operations that assume server-side session state (rare)

### When to Enable Affinity

Set `DisableAffinityCookie = false` for:

- Low-volume applications where throughput isn't critical
- Scenarios requiring server-side session affinity (uncommon in Dataverse)
- Debugging specific node behavior

## References

- [ServiceClient Discussion #312](https://github.com/microsoft/PowerPlatform-DataverseServiceClient/discussions/312) - Microsoft confirms order-of-magnitude improvement
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests) - Microsoft's guidance on disabling affinity cookie
- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update) - Performance optimization patterns

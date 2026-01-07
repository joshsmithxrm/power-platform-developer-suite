# ADR-0026: Structured Error Model

**Status:** Accepted
**Date:** 2026-01-06
**Authors:** Josh, Claude

## Context

Application Services throw exceptions when operations fail. Different UIs need to handle errors differently:

| UI | Error Display |
|----|--------------|
| CLI | Red error message to stderr, exit code |
| TUI | Modal dialog or status bar message |
| VS Code | Notification with action buttons |
| RPC | JSON error response with code and message |

Current state: Services throw mixed exception types (ArgumentException, InvalidOperationException, FaultException). This makes it difficult for UIs to:

- Display user-friendly messages (technical details leak through)
- Handle errors programmatically (retry on throttle, re-auth on expired token)
- Provide consistent behavior across interfaces

## Decision

### Structured Exception Hierarchy

Services throw `PpdsException` (or subclasses) with structured error information:

```csharp
public class PpdsException : Exception
{
    /// <summary>Machine-readable error code for programmatic handling.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Human-readable message safe to display to users.</summary>
    public required string UserMessage { get; init; }

    /// <summary>Severity level for UI display decisions.</summary>
    public PpdsSeverity Severity { get; init; } = PpdsSeverity.Error;

    /// <summary>Additional context for debugging (not shown to users).</summary>
    public IDictionary<string, object>? Context { get; init; }

    public PpdsException(string errorCode, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }
}

public enum PpdsSeverity { Info, Warning, Error }
```

### Specific Exception Types

```csharp
/// <summary>Authentication/authorization failures.</summary>
public class PpdsAuthException : PpdsException
{
    public bool RequiresReauthentication { get; init; }

    public PpdsAuthException(string errorCode, string userMessage)
        : base(errorCode, userMessage) { }
}

/// <summary>Dataverse throttling (429 responses).</summary>
public class PpdsThrottleException : PpdsException
{
    public TimeSpan RetryAfter { get; init; }

    public PpdsThrottleException(TimeSpan retryAfter)
        : base("THROTTLED", $"Rate limited. Retry after {retryAfter.TotalSeconds:F0} seconds.")
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>Input validation failures.</summary>
public class PpdsValidationException : PpdsException
{
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    public PpdsValidationException(IEnumerable<ValidationError> errors)
        : base("VALIDATION_FAILED", "One or more validation errors occurred.")
    {
        Errors = errors.ToList();
    }
}

public record ValidationError(string Field, string Message);
```

### Standard Error Codes

| Code | Meaning | Programmatic Action |
|------|---------|---------------------|
| `AUTH_EXPIRED` | Token expired | Trigger re-authentication flow |
| `AUTH_FAILED` | Authentication failed | Show login dialog |
| `THROTTLED` | Rate limited (429) | Wait `RetryAfter`, retry |
| `NOT_FOUND` | Resource not found | Show error, no retry |
| `VALIDATION_FAILED` | Invalid input | Show validation errors |
| `CONNECTION_FAILED` | Network error | Retry with backoff |
| `PERMISSION_DENIED` | Insufficient privileges | Show error, no retry |
| `CONFLICT` | Optimistic concurrency failure | Refresh and retry |
| `INTERNAL_ERROR` | Unexpected failure | Log details, show generic error |

### UI Error Handling Pattern

```csharp
// CLI error handler
try
{
    await service.ExecuteAsync(request, cancellationToken);
}
catch (PpdsThrottleException ex)
{
    AnsiConsole.MarkupLine($"[yellow]Rate limited. Waiting {ex.RetryAfter.TotalSeconds}s...[/]");
    await Task.Delay(ex.RetryAfter, cancellationToken);
    // Retry...
}
catch (PpdsValidationException ex)
{
    foreach (var error in ex.Errors)
        AnsiConsole.MarkupLine($"[red]{error.Field}: {error.Message}[/]");
    return ExitCodes.ValidationError;
}
catch (PpdsException ex)
{
    AnsiConsole.MarkupLine($"[red]Error: {ex.UserMessage}[/]");
    return ExitCodes.Error;
}

// TUI error handler
try
{
    await service.ExecuteAsync(request, cancellationToken);
}
catch (PpdsException ex)
{
    MessageBox.ErrorQuery("Error", ex.UserMessage, "OK");
}

// RPC error handler (serializes to JSON-RPC error)
try
{
    return await service.ExecuteAsync(request, cancellationToken);
}
catch (PpdsException ex)
{
    throw new JsonRpcException(
        code: MapErrorCode(ex.ErrorCode),
        message: ex.UserMessage,
        data: new { errorCode = ex.ErrorCode, context = ex.Context }
    );
}
```

## Consequences

### Positive

- **User-friendly messages** - `UserMessage` is always safe to display
- **Programmatic handling** - `ErrorCode` enables retry/re-auth logic
- **Consistent across UIs** - Same error info available everywhere
- **Debugging support** - `Context` provides details for troubleshooting
- **RPC-friendly** - Maps cleanly to JSON-RPC error responses

### Negative

- **Exception wrapping** - Services must catch and wrap lower-level exceptions
- **New exception types** - Additional classes to maintain

### Neutral

- **Gradual adoption** - Can migrate services incrementally
- **Existing patterns preserved** - FaultException from Dataverse still caught internally

## Implementation Guidelines

### Service Exception Wrapping

```csharp
public async Task<Result> ExecuteAsync(Request request, CancellationToken ct)
{
    try
    {
        // Business logic...
        return await _executor.ExecuteAsync(request, ct);
    }
    catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail.ErrorCode == -2147204784)
    {
        throw new PpdsAuthException("AUTH_EXPIRED", "Your session has expired. Please log in again.")
        {
            RequiresReauthentication = true,
            Context = new Dictionary<string, object> { ["faultCode"] = ex.Detail.ErrorCode }
        };
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
    {
        var retryAfter = ParseRetryAfter(ex);
        throw new PpdsThrottleException(retryAfter);
    }
    catch (Exception ex) when (ex is not PpdsException)
    {
        throw new PpdsException("INTERNAL_ERROR", "An unexpected error occurred. Please try again.")
        {
            Context = new Dictionary<string, object> { ["exception"] = ex.ToString() }
        };
    }
}
```

### UserMessage Guidelines

| Do | Don't |
|----|-------|
| "Your session has expired. Please log in again." | "FaultException: ErrorCode -2147204784" |
| "Rate limited. Retry after 30 seconds." | "HTTP 429 Too Many Requests" |
| "Account 'Contoso' not found." | "Entity with id 00000000-... does not exist" |
| "You don't have permission to delete this record." | "Principal ... lacks prvDeleteAccount" |

## References

- ADR-0015: Application Service Layer
- ADR-0020: Import Error Reporting (pattern for validation errors)
- ADR-0025: UI-Agnostic Progress Reporting

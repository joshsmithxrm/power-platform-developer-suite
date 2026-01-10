// Pattern: Service Layer
// Demonstrates: IProgressReporter, PpdsException, DI registration
// Related: ADR-0015, ADR-0025, ADR-0026
// Source: src/PPDS.Cli/Services/*, src/PPDS.Cli/Infrastructure/Errors/PpdsException.cs

// KEY PRINCIPLES:
// 1. Services own business logic - UIs are dumb views
// 2. Accept IProgressReporter for operations >1 second
// 3. Throw PpdsException with ErrorCode (machine) + UserMessage (human)
// 4. Register in AddCliApplicationServices() for DI consistency

using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;

// INTERFACE: Define service contract
public interface IDataExportService
{
    Task<ExportResult> ExportAsync(
        ExportOptions options,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}

// IMPLEMENTATION: Business logic lives here
public class DataExportService : IDataExportService
{
    private readonly IDataverseConnectionPool _connectionPool;
    private readonly ILogger<DataExportService> _logger;

    public DataExportService(
        IDataverseConnectionPool connectionPool,
        ILogger<DataExportService> logger)
    {
        _connectionPool = connectionPool;
        _logger = logger;
    }

    public async Task<ExportResult> ExportAsync(
        ExportOptions options,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        // PATTERN: Validate inputs, throw structured exceptions
        if (string.IsNullOrWhiteSpace(options.EntityName))
        {
            throw new PpdsValidationException(
                ErrorCodes.Validation.MissingRequired,
                "Entity name is required",
                new[] { new ValidationError("entityName", "Entity name cannot be empty") });
        }

        try
        {
            // PATTERN: Report progress for long operations
            progress?.Report(new ProgressUpdate("Connecting to Dataverse..."));

            await using var client = await _connectionPool.GetClientAsync(cancellationToken);

            progress?.Report(new ProgressUpdate("Fetching records..."));

            var records = await FetchRecordsAsync(client, options, cancellationToken);

            // PATTERN: Progress with percentage
            var totalRecords = records.Count;
            var exported = 0;

            foreach (var batch in records.Chunk(1000))
            {
                await WriteBatchAsync(batch, options.OutputPath, cancellationToken);
                exported += batch.Length;

                progress?.Report(new ProgressUpdate(
                    $"Exported {exported}/{totalRecords} records",
                    percentage: (double)exported / totalRecords * 100));
            }

            return new ExportResult
            {
                Success = true,
                RecordCount = totalRecords,
                OutputPath = options.OutputPath
            };
        }
        catch (FaultException<OrganizationServiceFault> ex) when (IsNotFoundError(ex))
        {
            // PATTERN: Convert known errors to specific exception types
            throw new PpdsNotFoundException(
                "Entity",
                options.EntityName,
                $"Entity '{options.EntityName}' not found in environment");
        }
        catch (FaultException<OrganizationServiceFault> ex) when (IsAuthError(ex))
        {
            // PATTERN: Auth errors with re-auth flag
            throw new PpdsAuthException(
                ErrorCodes.Auth.TokenExpired,
                "Authentication expired during export. Please re-authenticate.",
                ex)
            {
                RequiresReauthentication = true
            };
        }
        catch (IOException ex)
        {
            // PATTERN: Wrap IO errors with user-friendly message
            throw new PpdsException(
                ErrorCodes.IO.WriteError,
                $"Failed to write export file: {ex.Message}",
                ex)
            {
                Severity = PpdsSeverity.Error,
                Context = new Dictionary<string, object>
                {
                    ["outputPath"] = options.OutputPath,
                    ["entityName"] = options.EntityName
                }
            };
        }
    }

    private static bool IsNotFoundError(FaultException<OrganizationServiceFault> ex)
        => ex.Detail?.ErrorCode == -2147088239; // Entity not found

    private static bool IsAuthError(FaultException<OrganizationServiceFault> ex)
        => ex.Detail?.ErrorCode == -2147180286;
}

// EXCEPTION TYPES: Use specific types for programmatic handling
public class PpdsException : Exception
{
    public string ErrorCode { get; init; }        // Machine-readable
    public string UserMessage { get; init; }      // Safe to display
    public PpdsSeverity Severity { get; init; }   // UI display decision
    public IDictionary<string, object>? Context { get; init; }  // Debug info
}

public class PpdsAuthException : PpdsException
{
    public bool RequiresReauthentication { get; init; }
}

public class PpdsValidationException : PpdsException
{
    public IReadOnlyList<ValidationError> Errors { get; init; }
}

public class PpdsNotFoundException : PpdsException
{
    public string ResourceType { get; init; }
    public string ResourceId { get; init; }
}

// DI REGISTRATION: Add to this method for CLI/TUI/RPC consistency
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCliApplicationServices(
        this IServiceCollection services)
    {
        // PATTERN: Register services here - single source of truth
        services.AddSingleton<IDataExportService, DataExportService>();
        services.AddSingleton<IDataImportService, DataImportService>();
        services.AddSingleton<IPluginService, PluginService>();
        // ... all application services

        return services;
    }
}

// ANTI-PATTERNS TO AVOID:
//
// BAD: Business logic in UI
// public class ExportCommand {
//     void Execute() {
//         var records = FetchRecords();  // WRONG - logic in command
//         WriteFile(records);            // Move to service
//     }
// }
//
// BAD: Raw exceptions without ErrorCode
// throw new Exception("Export failed");  // WRONG - no programmatic handling
//
// BAD: Console.WriteLine in services
// Console.WriteLine("Exporting...");  // WRONG - use IProgressReporter
//
// BAD: Catching all exceptions silently
// catch (Exception) { return null; }  // WRONG - surface errors properly

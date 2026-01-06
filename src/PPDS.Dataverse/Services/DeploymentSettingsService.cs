using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for deployment settings file operations.
/// </summary>
public class DeploymentSettingsService : IDeploymentSettingsService
{
    private readonly IEnvironmentVariableService _envVarService;
    private readonly IConnectionReferenceService _connectionRefService;
    private readonly ILogger<DeploymentSettingsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentSettingsService"/> class.
    /// </summary>
    public DeploymentSettingsService(
        IEnvironmentVariableService envVarService,
        IConnectionReferenceService connectionRefService,
        ILogger<DeploymentSettingsService> logger)
    {
        _envVarService = envVarService ?? throw new ArgumentNullException(nameof(envVarService));
        _connectionRefService = connectionRefService ?? throw new ArgumentNullException(nameof(connectionRefService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DeploymentSettingsFile> GenerateAsync(
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Generating deployment settings for solution '{Solution}'", solutionName);

        // Get current environment variables and connection references from the solution
        var envVars = await _envVarService.ListAsync(solutionName, cancellationToken);
        var connectionRefs = await _connectionRefService.ListAsync(solutionName, cancellationToken: cancellationToken);

        var settings = new DeploymentSettingsFile
        {
            EnvironmentVariables = envVars
                .Where(ev => ev.Type != "Secret") // Don't include secrets in deployment settings
                .Select(ev => new EnvironmentVariableEntry
                {
                    SchemaName = ev.SchemaName,
                    Value = ev.CurrentValue ?? ev.DefaultValue ?? string.Empty
                })
                .OrderBy(ev => ev.SchemaName, StringComparer.Ordinal)
                .ToList(),

            ConnectionReferences = connectionRefs
                .Select(cr => new ConnectionReferenceEntry
                {
                    LogicalName = cr.LogicalName,
                    ConnectionId = cr.ConnectionId ?? string.Empty,
                    ConnectorId = cr.ConnectorId ?? string.Empty
                })
                .OrderBy(cr => cr.LogicalName, StringComparer.Ordinal)
                .ToList()
        };

        _logger.LogDebug(
            "Generated settings: {EnvVarCount} environment variables, {CRCount} connection references",
            settings.EnvironmentVariables.Count,
            settings.ConnectionReferences.Count);

        return settings;
    }

    /// <inheritdoc />
    public async Task<DeploymentSettingsSyncResult> SyncAsync(
        string solutionName,
        DeploymentSettingsFile? existingSettings,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Syncing deployment settings for solution '{Solution}'", solutionName);

        // Get current state from environment
        var currentEnvVars = await _envVarService.ListAsync(solutionName, cancellationToken);
        var currentConnectionRefs = await _connectionRefService.ListAsync(solutionName, cancellationToken: cancellationToken);

        // Build current schema names sets
        var currentEnvVarNames = currentEnvVars
            .Where(ev => ev.Type != "Secret")
            .Select(ev => ev.SchemaName)
            .ToHashSet(StringComparer.Ordinal);

        var currentCRNames = currentConnectionRefs
            .Select(cr => cr.LogicalName)
            .ToHashSet(StringComparer.Ordinal);

        // Process environment variables
        var existingEnvVars = existingSettings?.EnvironmentVariables
            .ToDictionary(ev => ev.SchemaName, ev => ev, StringComparer.Ordinal)
            ?? new Dictionary<string, EnvironmentVariableEntry>(StringComparer.Ordinal);

        var syncedEnvVars = new List<EnvironmentVariableEntry>();
        var evStats = new SyncStatisticsBuilder();

        foreach (var ev in currentEnvVars.Where(ev => ev.Type != "Secret"))
        {
            if (existingEnvVars.TryGetValue(ev.SchemaName, out var existing))
            {
                // Preserve existing value
                syncedEnvVars.Add(new EnvironmentVariableEntry
                {
                    SchemaName = ev.SchemaName,
                    Value = existing.Value
                });
                evStats.Preserved++;
            }
            else
            {
                // New entry - use current environment value
                syncedEnvVars.Add(new EnvironmentVariableEntry
                {
                    SchemaName = ev.SchemaName,
                    Value = ev.CurrentValue ?? ev.DefaultValue ?? string.Empty
                });
                evStats.Added++;
            }
        }

        // Count removed entries
        evStats.Removed = existingEnvVars.Keys.Count(name => !currentEnvVarNames.Contains(name));

        // Process connection references
        var existingCRs = existingSettings?.ConnectionReferences
            .ToDictionary(cr => cr.LogicalName, cr => cr, StringComparer.Ordinal)
            ?? new Dictionary<string, ConnectionReferenceEntry>(StringComparer.Ordinal);

        var syncedCRs = new List<ConnectionReferenceEntry>();
        var crStats = new SyncStatisticsBuilder();

        foreach (var cr in currentConnectionRefs)
        {
            if (existingCRs.TryGetValue(cr.LogicalName, out var existing))
            {
                // Preserve existing values
                syncedCRs.Add(new ConnectionReferenceEntry
                {
                    LogicalName = cr.LogicalName,
                    ConnectionId = existing.ConnectionId,
                    ConnectorId = existing.ConnectorId
                });
                crStats.Preserved++;
            }
            else
            {
                // New entry - use current environment value
                syncedCRs.Add(new ConnectionReferenceEntry
                {
                    LogicalName = cr.LogicalName,
                    ConnectionId = cr.ConnectionId ?? string.Empty,
                    ConnectorId = cr.ConnectorId ?? string.Empty
                });
                crStats.Added++;
            }
        }

        // Count removed entries
        crStats.Removed = existingCRs.Keys.Count(name => !currentCRNames.Contains(name));

        // Sort for deterministic output
        var settings = new DeploymentSettingsFile
        {
            EnvironmentVariables = syncedEnvVars
                .OrderBy(ev => ev.SchemaName, StringComparer.Ordinal)
                .ToList(),
            ConnectionReferences = syncedCRs
                .OrderBy(cr => cr.LogicalName, StringComparer.Ordinal)
                .ToList()
        };

        _logger.LogDebug(
            "Sync complete - EnvVars: +{EVAdded} -{EVRemoved} ={EVPreserved}, CRs: +{CRAdded} -{CRRemoved} ={CRPreserved}",
            evStats.Added, evStats.Removed, evStats.Preserved,
            crStats.Added, crStats.Removed, crStats.Preserved);

        return new DeploymentSettingsSyncResult
        {
            Settings = settings,
            EnvironmentVariables = evStats.Build(),
            ConnectionReferences = crStats.Build()
        };
    }

    /// <inheritdoc />
    public async Task<DeploymentSettingsValidation> ValidateAsync(
        string solutionName,
        DeploymentSettingsFile settings,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Validating deployment settings for solution '{Solution}'", solutionName);

        var issues = new List<ValidationIssue>();

        // Get current state from environment
        var currentEnvVars = await _envVarService.ListAsync(solutionName, cancellationToken);
        var currentConnectionRefs = await _connectionRefService.ListAsync(solutionName, cancellationToken: cancellationToken);

        var currentEnvVarNames = currentEnvVars
            .ToDictionary(ev => ev.SchemaName, ev => ev, StringComparer.Ordinal);
        var currentCRNames = currentConnectionRefs
            .ToDictionary(cr => cr.LogicalName, cr => cr, StringComparer.Ordinal);

        // Validate environment variables
        foreach (var ev in settings.EnvironmentVariables)
        {
            if (!currentEnvVarNames.TryGetValue(ev.SchemaName, out var current))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    EntryType = "EnvironmentVariable",
                    Name = ev.SchemaName,
                    Message = "Not found in solution - entry will be ignored during import"
                });
            }
            else if (string.IsNullOrEmpty(ev.Value) && current.IsRequired)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    EntryType = "EnvironmentVariable",
                    Name = ev.SchemaName,
                    Message = "Required environment variable has empty value"
                });
            }
        }

        // Check for missing required environment variables
        foreach (var ev in currentEnvVars.Where(ev => ev.IsRequired && ev.Type != "Secret"))
        {
            if (!settings.EnvironmentVariables.Any(e =>
                string.Equals(e.SchemaName, ev.SchemaName, StringComparison.Ordinal)))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    EntryType = "EnvironmentVariable",
                    Name = ev.SchemaName,
                    Message = "Required environment variable missing from settings file"
                });
            }
        }

        // Validate connection references
        foreach (var cr in settings.ConnectionReferences)
        {
            if (!currentCRNames.ContainsKey(cr.LogicalName))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    EntryType = "ConnectionReference",
                    Name = cr.LogicalName,
                    Message = "Not found in solution - entry will be ignored during import"
                });
            }
            else if (string.IsNullOrEmpty(cr.ConnectionId))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    EntryType = "ConnectionReference",
                    Name = cr.LogicalName,
                    Message = "Missing ConnectionId - will prompt during import"
                });
            }
        }

        // Check for missing connection references
        foreach (var cr in currentConnectionRefs)
        {
            if (!settings.ConnectionReferences.Any(c =>
                string.Equals(c.LogicalName, cr.LogicalName, StringComparison.Ordinal)))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    EntryType = "ConnectionReference",
                    Name = cr.LogicalName,
                    Message = "Connection reference missing from settings file - will prompt during import"
                });
            }
        }

        _logger.LogDebug("Validation complete: {IssueCount} issues found", issues.Count);

        return new DeploymentSettingsValidation { Issues = issues };
    }

    /// <summary>
    /// Helper to build sync statistics.
    /// </summary>
    private sealed class SyncStatisticsBuilder
    {
        public int Added { get; set; }
        public int Removed { get; set; }
        public int Preserved { get; set; }

        public SyncStatistics Build() => new()
        {
            Added = Added,
            Removed = Removed,
            Preserved = Preserved
        };
    }
}

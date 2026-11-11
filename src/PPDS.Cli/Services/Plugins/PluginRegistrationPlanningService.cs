using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Services.Plugins;

/// <summary>Read-only registration preflight shared by real deployment and preview.</summary>
internal static class PluginRegistrationPlanningService
{
    internal static void ValidateAssembly(PluginAssemblyConfig assembly, CancellationToken cancellationToken)
    {
        foreach (var type in assembly.Types)
        foreach (var step in type.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            step.OperationIntent = PluginRegistrationContract.FileIntent(step.OperationIntent);
            if (string.IsNullOrWhiteSpace(step.Message))
                throw new PpdsException(ErrorCodes.Validation.InvalidValue, "Step message is required.");
            _ = PluginRegistrationNormalizer.NormalizeStep(step, PluginRegistrationContract.FileIntent(step.OperationIntent));
            foreach (var image in step.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.OperationIntent = step.OperationIntent;
                _ = PluginRegistrationNormalizer.NormalizeImage(image, step.Message, step.OperationIntent);
            }
        }
    }

    internal static async Task ValidateRunAsUsersAsync(IPluginRegistrationService registration,
        IEnumerable<PluginAssemblyConfig> assemblies, CancellationToken cancellationToken,
        IProgressReporter? progress = null)
    {
        try
        {
            progress?.ReportPhase("Validating plugin user contexts");
            var checkedSelectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in assemblies.SelectMany(a => a.Types).SelectMany(t => t.Steps))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var desired = PluginRegistrationNormalizer.NormalizeStep(step, PluginRegistrationContract.FileIntent(step.OperationIntent));
                if (desired.TryGetValue("runAsUser", out var value) && value is string selector && checkedSelectors.Add(selector))
                    await registration.ResolveRunAsUserAsync(selector, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(ExceptionMapper.Map(ex).Code,
                "Plugin user context validation could not be completed.", ex);
        }
    }

    /// <summary>
    /// Check every affected step before any assembly/package mutation. This is a read-only
    /// observation, not a reservation: the registration service checks again at the write boundary.
    /// </summary>
    internal static async Task ValidateStepFieldCapabilitiesAsync(IPluginRegistrationService registration,
        IEnumerable<PluginAssemblyConfig> assemblies, IReadOnlyDictionary<string, string>? packageNames,
        CancellationToken cancellationToken, IProgressReporter? progress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.ReportPhase("Validating plugin step field capabilities");
            foreach (var assembly in assemblies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configured = assembly.Types.SelectMany(t => t.Steps.Select(s => (Type: t, Step: s))).ToList();
                if (!configured.Any(p => PluginRegistrationNormalizer.NormalizeStep(p.Step, p.Step.OperationIntent)
                        .ContainsKey("canUseReadOnlyConnection"))) continue;

                Guid? assemblyId;
                if (string.Equals(assembly.Type, "Nuget", StringComparison.OrdinalIgnoreCase))
                {
                    if (packageNames is null || !packageNames.TryGetValue(assembly.Name, out var packageName))
                        throw new PpdsException(ErrorCodes.Plugin.FieldCapabilityUnknown,
                            "Step capability preflight requires the inspected package identity.");
                    var package = await registration.GetPackageByNameAsync(packageName, cancellationToken);
                    assemblyId = package is null ? null : await registration.GetAssemblyIdForPackageAsync(
                        package.Id, assembly.Name, cancellationToken);
                    // An existing package may gain a new assembly during upload. A completed
                    // scoped lookup with no match means its configured steps need create capability.
                    // Deployment rematches after upload and the service rechecks the actual row.
                }
                else
                    assemblyId = (await registration.GetAssemblyByNameAsync(assembly.Name, cancellationToken))?.Id;

                var existing = new List<(PluginTypeInfo Type, PluginStepInfo Step)>();
                if (assemblyId.HasValue)
                {
                    var types = await registration.ListTypesForAssemblyAsync(assemblyId.Value, cancellationToken);
                    if (types.GroupBy(t => t.TypeName, StringComparer.Ordinal).Any(g => g.Count() > 1))
                        throw new PpdsException(ErrorCodes.Operation.Duplicate,
                            $"Assembly '{assembly.Name}' has duplicate plugin type names; step capability preflight is ambiguous.");
                    foreach (var type in types)
                    {
                        var steps = await registration.ListStepsForTypeAsync(type.Id, cancellationToken: cancellationToken);
                        existing.AddRange(steps.Select(s => (type, s)));
                    }
                }

                // Use the same functional matching rules as deployment, including duplicate-aware pairing.
                foreach (var match in PluginStepMatcher.Match(configured, existing))
                    if (match.Config is not null)
                        await registration.ValidateStepFieldCapabilitiesAsync(match.Config, match.Env?.Id, cancellationToken, progress);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex)
        {
            throw new PpdsException(ExceptionMapper.Map(ex).Code,
                "Plugin step field capability validation could not be completed.", ex);
        }
    }
}

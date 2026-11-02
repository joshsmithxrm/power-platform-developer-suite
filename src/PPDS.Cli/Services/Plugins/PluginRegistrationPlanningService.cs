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
            step.OperationIntent = RegistrationOperationIntent.Deploy;
            if (string.IsNullOrWhiteSpace(step.Message))
                throw new PpdsException(ErrorCodes.Validation.InvalidValue, "Step message is required.");
            _ = PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Deploy);
            foreach (var image in step.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                image.OperationIntent = RegistrationOperationIntent.Deploy;
                _ = PluginRegistrationNormalizer.NormalizeImage(image, step.Message, RegistrationOperationIntent.Deploy);
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
                var desired = PluginRegistrationNormalizer.NormalizeStep(step, RegistrationOperationIntent.Deploy);
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
}

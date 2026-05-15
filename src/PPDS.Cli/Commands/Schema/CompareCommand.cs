using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Schema;
using PPDS.Cli.Services.Schema.Models;
using PPDS.Cli.Services.Schema.Snapshots;
using PPDS.Dataverse.Metadata;
using PPDS.Migration.Formats;

namespace PPDS.Cli.Commands.Schema;

/// <summary>
/// <c>ppds schema compare</c> — diff a data package or environment against
/// a target environment and report differences with severity classification.
/// </summary>
public static class CompareCommand
{
    private static readonly Option<FileInfo?> DataOption = new("--data")
    {
        Description = "[Required with --environment] Path to a data package zip whose schema is compared against the target environment."
    };

    private static readonly Option<string?> EnvOption = new("--environment", "-e")
    {
        Description = "[Required with --data] Target environment - URL, friendly name, unique name, or ID."
    };

    private static readonly Option<string?> SourceOption = new("--source-env", "-se")
    {
        Description = "[Required with --target-env] Source environment for env-to-env comparison."
    };

    private static readonly Option<string?> TargetOption = new("--target-env", "-te")
    {
        Description = "[Required with --source-env] Target environment for env-to-env comparison."
    };

    private static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name (defaults to active profile)."
    };

    /// <summary>
    /// Creates the <c>schema compare</c> command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command(
            "compare",
            "Compare schema between a data package and an environment, or between two environments. "
            + "Differences are classified by severity (Error / Warning / Info); the highest severity "
            + "determines the exit code. Output format selectable via -f (Text / Json / Csv).")
        {
            DataOption, EnvOption, SourceOption, TargetOption, ProfileOption
        };

        GlobalOptions.AddToCommand(command);

        command.Validators.Add(result =>
        {
            var data = result.GetValue(DataOption);
            var env = result.GetValue(EnvOption);
            var source = result.GetValue(SourceOption);
            var target = result.GetValue(TargetOption);

            var packageMode = data is not null || env is not null;
            var envMode = source is not null || target is not null;

            if (packageMode && envMode)
            {
                result.AddError("--data/--environment cannot be combined with --source-env/--target-env.");
                return;
            }
            if (!packageMode && !envMode)
            {
                result.AddError("Provide either (--data + --environment) for package-vs-env, or (--source-env + --target-env) for env-vs-env.");
                return;
            }
            if (packageMode && (data is null || env is null))
            {
                result.AddError("--data and --environment must be supplied together.");
            }
            if (envMode && (source is null || target is null))
            {
                result.AddError("--source-env and --target-env must be supplied together.");
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var data = parseResult.GetValue(DataOption);
            var env = parseResult.GetValue(EnvOption);
            var source = parseResult.GetValue(SourceOption);
            var target = parseResult.GetValue(TargetOption);
            var profile = parseResult.GetValue(ProfileOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(data?.FullName, env, source, target, profile, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? dataPath,
        string? env,
        string? sourceEnv,
        string? targetEnv,
        string? profile,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            SchemaSnapshot sourceSnapshot;
            SchemaSnapshot targetSnapshot;
            IReadOnlyList<string> targetExtraEntities;

            Action<string>? progress = globalOptions.IsJsonMode
                ? null
                : msg => Console.Error.WriteLine(msg);

            if (dataPath is not null && env is not null)
            {
                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Reading schema from data package: {dataPath}");
                }
                var packageLoader = new DataPackageSnapshotLoader(dataPath, new CmtSchemaReader());
                sourceSnapshot = await packageLoader.LoadAsync(null, cancellationToken).ConfigureAwait(false);

                await using var targetProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                    profile,
                    env,
                    globalOptions.Verbose,
                    globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var targetMetadata = targetProvider.GetRequiredService<IMetadataQueryService>();
                var targetConnInfo = targetProvider.GetRequiredService<ResolvedConnectionInfo>();

                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Loading target schema from {targetConnInfo.EnvironmentUrl}...");
                }

                var entityFilter = sourceSnapshot.Entities.Select(e => e.LogicalName).ToList();
                var targetLoader = new EnvironmentSnapshotLoader(targetMetadata, $"env:{targetConnInfo.EnvironmentUrl}", progress);
                targetSnapshot = await targetLoader.LoadAsync(entityFilter, cancellationToken).ConfigureAwait(false);
                targetExtraEntities = targetLoader.UnloadedEntities;

                var service = targetProvider.GetRequiredService<ISchemaComparisonService>();
                var report = BuildReport(service, sourceSnapshot, targetSnapshot, targetExtraEntities);
                return EmitAndExit(writer, report, globalOptions);
            }

            // env-to-env mode (validator guarantees both are non-null)
            var (sourceProvider, targetEnvProvider) = await ProfileServiceFactory.CreateForCopyAsync(
                profile,
                sourceProfile: null,
                targetProfile: null,
                sourceEnv: sourceEnv!,
                targetEnv: targetEnv!,
                verbose: globalOptions.Verbose,
                debug: globalOptions.Debug,
                deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await using (sourceProvider)
            await using (targetEnvProvider)
            {
                var sourceInfo = sourceProvider.GetRequiredService<ResolvedConnectionInfo>();
                var targetInfo = targetEnvProvider.GetRequiredService<ResolvedConnectionInfo>();

                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Loading source schema from {sourceInfo.EnvironmentUrl}...");
                }
                var sourceLoader = new EnvironmentSnapshotLoader(
                    sourceProvider.GetRequiredService<IMetadataQueryService>(),
                    $"env:{sourceInfo.EnvironmentUrl}",
                    progress);
                sourceSnapshot = await sourceLoader.LoadAsync(null, cancellationToken).ConfigureAwait(false);

                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Loading target schema from {targetInfo.EnvironmentUrl}...");
                }
                var entityFilter = sourceSnapshot.Entities.Select(e => e.LogicalName).ToList();
                var targetLoader = new EnvironmentSnapshotLoader(
                    targetEnvProvider.GetRequiredService<IMetadataQueryService>(),
                    $"env:{targetInfo.EnvironmentUrl}",
                    progress);
                targetSnapshot = await targetLoader.LoadAsync(entityFilter, cancellationToken).ConfigureAwait(false);
                targetExtraEntities = targetLoader.UnloadedEntities;

                var service = sourceProvider.GetRequiredService<ISchemaComparisonService>();
                var report = BuildReport(service, sourceSnapshot, targetSnapshot, targetExtraEntities);
                return EmitAndExit(writer, report, globalOptions);
            }
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "comparing schemas", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Run the comparison and merge in <see cref="DiffKind.ExtraEntity"/> diffs for
    /// entities the loader skipped via the source-driven filter. Without this merge,
    /// the filter would mask the only AC-relevant "info" output.
    /// </summary>
    internal static SchemaCompareReport BuildReport(
        ISchemaComparisonService service,
        SchemaSnapshot source,
        SchemaSnapshot target,
        IReadOnlyList<string> targetExtraEntityNames)
    {
        var report = service.Compare(source, target);
        if (targetExtraEntityNames.Count == 0)
        {
            return report;
        }

        var sourceNames = new HashSet<string>(
            source.Entities.Select(e => e.LogicalName), StringComparer.OrdinalIgnoreCase);

        var extras = targetExtraEntityNames
            .Where(n => !sourceNames.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SchemaDifference
            {
                Severity = DiffSeverity.Info,
                Kind = DiffKind.ExtraEntity,
                Entity = n,
                Message = $"Entity '{n}' exists in target but not in source."
            });

        var merged = report.Differences.Concat(extras).ToList();
        return new SchemaCompareReport
        {
            Source = report.Source,
            Target = report.Target,
            ComparedAt = report.ComparedAt,
            Differences = merged
        };
    }

    private static int EmitAndExit(
        PPDS.Cli.Infrastructure.Output.IOutputWriter writer,
        SchemaCompareReport report,
        GlobalOptionValues globalOptions)
    {
        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(report);
        }
        else
        {
            WriteTextReport(report);
        }
        return ExitCodeFor(report.HighestSeverity);
    }

    internal static int ExitCodeFor(DiffSeverity? highest) => highest switch
    {
        null => ExitCodes.Success,
        DiffSeverity.Info => ExitCodes.Success,
        DiffSeverity.Warning => ExitCodes.PartialSuccess,
        DiffSeverity.Error => ExitCodes.ValidationError,
        _ => ExitCodes.Success
    };

    private static void WriteTextReport(SchemaCompareReport report)
    {
        var s = report.Summary;
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Schema comparison: {report.Source} → {report.Target}");
        Console.Error.WriteLine($"  {s.Errors} error(s), {s.Warnings} warning(s), {s.Infos} info");
        Console.Error.WriteLine();

        if (report.Differences.Count == 0)
        {
            Console.Error.WriteLine("No schema differences detected.");
            return;
        }

        foreach (var group in report.Differences
            .GroupBy(d => d.Severity)
            .OrderByDescending(g => g.Key))
        {
            Console.Error.WriteLine($"[{group.Key.ToString().ToUpperInvariant()}]");
            foreach (var diff in group)
            {
                Console.Error.WriteLine($"  {diff.Kind}: {diff.Message}");
            }
            Console.Error.WriteLine();
        }
    }
}

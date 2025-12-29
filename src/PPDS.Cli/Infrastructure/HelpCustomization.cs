using System.CommandLine;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Transforms required option display from (REQUIRED) suffix to [Required] prefix.
/// The default suffix can wrap awkwardly in narrow terminals; inline prefix is more scannable.
/// </summary>
public static class HelpCustomization
{
    public static void ApplyRequiredOptionStyle(Command command)
    {
        ApplyToCommand(command);
    }

    private static void ApplyToCommand(Command command)
    {
        var requiredOptions = new List<Option>();

        foreach (var option in command.Options)
        {
            if (option.Required)
            {
                requiredOptions.Add(option);

                var originalDescription = option.Description ?? string.Empty;
                if (!originalDescription.StartsWith("[Required]"))
                {
                    option.Description = $"[Required] {originalDescription}".Trim();
                }

                // Required=false hides the default suffix; we show [Required] in description instead
                option.Required = false;
            }
        }

        // Option validators only run when the option is present on command line,
        // so we need command-level validation to catch missing required options
        if (requiredOptions.Count > 0)
        {
            command.Validators.Add(result =>
            {
                foreach (var option in requiredOptions)
                {
                    var optionResult = result.GetResult(option);
                    if (optionResult is null || optionResult.Tokens.Count == 0)
                    {
                        result.AddError($"Option '{option.Name}' is required.");
                    }
                }
            });
        }

        foreach (var subcommand in command.Subcommands)
        {
            ApplyToCommand(subcommand);
        }
    }
}

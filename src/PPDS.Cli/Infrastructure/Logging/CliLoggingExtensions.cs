using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PPDS.Cli.Infrastructure.Logging;

/// <summary>
/// Extension methods for configuring CLI logging.
/// </summary>
public static class CliLoggingExtensions
{
    /// <summary>
    /// Adds CLI logging services with the appropriate provider based on output format.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Logger configuration options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCliLogging(
        this IServiceCollection services,
        CliLoggerOptions options)
    {
        // Create and register the log context
        var context = new LogContext
        {
            CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString("D")
        };
        services.AddSingleton(context);

        // Register logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(options.MinimumLevel);
            builder.ClearProviders();

            if (options.UseJsonFormat)
            {
                builder.AddProvider(new ConsoleJsonLoggerProvider(options, context));
            }
            else
            {
                builder.AddProvider(new ConsoleTextLoggerProvider(options, context));
            }
        });

        return services;
    }

    /// <summary>
    /// Adds CLI logging services with the appropriate provider based on output format.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Action to configure logger options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCliLogging(
        this IServiceCollection services,
        Action<CliLoggerOptions> configure)
    {
        var options = new CliLoggerOptions();
        configure(options);
        return services.AddCliLogging(options);
    }
}

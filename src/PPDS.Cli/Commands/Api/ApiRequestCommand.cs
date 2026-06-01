using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Services.WebApi;

namespace PPDS.Cli.Commands.Api;

public static class ApiRequestCommand
{
    public static Command Create()
    {
        var pathOption = new Option<string>("--path")
        {
            Description = "Relative URL path (e.g., /api/data/v9.2/accounts)",
            Required = true
        };
        var methodOption = new Option<string?>("--method", "-X")
        {
            Description = "HTTP method (default: GET)"
        };
        var bodyOption = new Option<string?>("--body")
        {
            Description = "Request body as inline JSON string"
        };
        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Request body from file (mutually exclusive with --body)"
        };
        var headerOption = new Option<string[]?>("--header", "-H")
        {
            Description = "Additional header, repeatable (format: \"Name: Value\")",
            AllowMultipleArgumentsPerToken = false
        };
        headerOption.Arity = ArgumentArity.ZeroOrMore;
        var includeOption = new Option<bool>("--include", "-i")
        {
            Description = "Include HTTP status line and response headers in output"
        };
        var environmentOption = new Option<string?>("--environment", "-env")
        {
            Description = "Target environment URL (default: active profile)"
        };
        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Bypass write protection on production environments"
        };

        var command = new Command("request", "Send a raw HTTP request to the Dataverse Web API")
        {
            pathOption,
            methodOption,
            bodyOption,
            bodyFileOption,
            headerOption,
            includeOption,
            environmentOption,
            confirmOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathOption)!;
            var method = parseResult.GetValue(methodOption);
            var body = parseResult.GetValue(bodyOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);
            var headers = parseResult.GetValue(headerOption);
            var include = parseResult.GetValue(includeOption);
            var environment = parseResult.GetValue(environmentOption);
            var confirm = parseResult.GetValue(confirmOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                path, method, body, bodyFile, headers, include,
                environment, confirm, globalOptions, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Validates raw inputs before any I/O. Returns an error message + exit code, or null if valid.
    /// Also returns parsed method and headers on success.
    /// </summary>
    internal static (string Error, int ExitCode)? ValidateInputs(
        string path,
        string? method,
        string? body,
        string? bodyFile,
        string[]? headers,
        out HttpMethod httpMethod,
        out Dictionary<string, string>? parsedHeaders)
    {
        httpMethod = HttpMethod.Get;
        parsedHeaders = null;

        if (!path.StartsWith('/'))
            return ("Path must start with '/'. Example: /api/data/v9.2/accounts", ExitCodes.InvalidArguments);

        if (body != null && bodyFile != null)
            return ("Cannot specify both --body and --body-file.", ExitCodes.InvalidArguments);

        if (!string.IsNullOrEmpty(method))
        {
            var valid = new[] { "GET", "POST", "PATCH", "PUT", "DELETE", "HEAD", "OPTIONS" };
            if (!valid.Contains(method.ToUpperInvariant()))
                return ($"Invalid HTTP method '{method}'. Use GET, POST, PATCH, PUT, DELETE, HEAD, or OPTIONS.", ExitCodes.InvalidArguments);
            httpMethod = new HttpMethod(method.ToUpperInvariant());
        }

        if (headers?.Length > 0)
        {
            parsedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                var idx = header.IndexOf(':');
                if (idx <= 0)
                    return ($"Invalid header format '{header}'. Expected 'Name: Value'.", ExitCodes.InvalidArguments);
                parsedHeaders[header[..idx].Trim()] = header[(idx + 1)..].Trim();
            }
        }

        return null;
    }

    private static async Task<int> ExecuteAsync(
        string path,
        string? method,
        string? body,
        string? bodyFile,
        string[]? headers,
        bool include,
        string? environment,
        bool confirm,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var validation = ValidateInputs(path, method, body, bodyFile, headers,
            out var httpMethod, out var parsedHeaders);
        if (validation.HasValue)
        {
            Console.Error.WriteLine($"Error: {validation.Value.Error}");
            return validation.Value.ExitCode;
        }

        // Validation: body file exists
        if (bodyFile != null)
        {
            if (!File.Exists(bodyFile))
            {
                Console.Error.WriteLine($"Error: Body file not found: '{bodyFile}'");
                return ExitCodes.NotFoundError;
            }
            body = await File.ReadAllTextAsync(bodyFile, cancellationToken);
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                null,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
            var envConfigService = serviceProvider.GetRequiredService<IEnvironmentConfigService>();
            var apiService = serviceProvider.GetRequiredService<IRawWebApiService>();

            var envConfig = await envConfigService.GetConfigAsync(connectionInfo.EnvironmentUrl, cancellationToken);
            var envType = envConfig?.Type ?? EnvironmentType.Unknown;
            var protectionLevel = envConfig?.Protection ?? DmlSafetyGuard.DetectProtectionLevel(envType);

            var request = new RawWebApiRequest
            {
                EnvironmentUrl = connectionInfo.EnvironmentUrl,
                Path = path,
                Method = httpMethod,
                Body = body,
                Headers = parsedHeaders,
                IsConfirmed = confirm,
                ProtectionLevel = protectionLevel
            };

            var response = await apiService.SendAsync(request, cancellationToken: cancellationToken);

            foreach (var line in FormatResponsePreamble(response, include))
                Console.WriteLine(line);

            if (response.IsSuccess)
            {
                if (!string.IsNullOrEmpty(response.Body))
                    Console.WriteLine(response.Body);
                return ExitCodes.Success;
            }
            else
            {
                Console.Error.WriteLine(response.Body);
                return 1; // non-2xx per spec
            }
        }
        catch (PpdsException ex) when (ex.ErrorCode == "Api.WriteBlocked")
        {
            Console.Error.WriteLine($"Error: {ex.UserMessage}");
            return ExitCodes.Failure; // 2 per spec
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3; // auth/connectivity failure per spec
        }
    }

    /// <summary>
    /// Returns lines for the HTTP status line and response headers when --include is set.
    /// Exposed internal for unit testing of AC-05.
    /// </summary>
    internal static IEnumerable<string> FormatResponsePreamble(RawWebApiResponse response, bool include)
    {
        if (!include)
            yield break;

        yield return $"HTTP/1.1 {response.StatusCode} {response.ReasonPhrase}";
        foreach (var (key, value) in response.Headers)
            yield return $"{key}: {value}";
        yield return string.Empty;
    }
}

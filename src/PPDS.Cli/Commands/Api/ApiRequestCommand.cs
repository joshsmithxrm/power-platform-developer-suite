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
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Authentication profile to use (default: active profile)"
        };
        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Bypass write protection on production environments"
        };

        var command = new Command(
            "request",
            "Send a raw HTTP request to the Dataverse Web API. " +
            "Note: on Git Bash/MSYS a leading-slash --path (e.g. /api/data/v9.2/accounts) is rewritten " +
            "into a Windows path before it reaches the CLI; prefix with MSYS_NO_PATHCONV=1, set " +
            "MSYS2_ARG_CONV_EXCL='*', or run from PowerShell/cmd.")
        {
            pathOption,
            methodOption,
            bodyOption,
            bodyFileOption,
            headerOption,
            includeOption,
            environmentOption,
            profileOption,
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
            var profile = parseResult.GetValue(profileOption);
            var confirm = parseResult.GetValue(confirmOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                path, method, body, bodyFile, headers, include,
                environment, profile, confirm, globalOptions, cancellationToken);
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
        {
            var message = "Path must start with '/'. Example: /api/data/v9.2/accounts";
            if (LooksLikeShellMangledPath(path))
                message += " It looks like your shell rewrote the path. On Git Bash/MSYS, prefix the command with " +
                           "`MSYS_NO_PATHCONV=1`, set `MSYS2_ARG_CONV_EXCL='*'`, or run from PowerShell/cmd.";
            return (message, ExitCodes.InvalidArguments);
        }

        if (body != null && bodyFile != null)
            return ("Cannot specify both --body and --body-file.", ExitCodes.InvalidArguments);

        if (!string.IsNullOrEmpty(method))
        {
            var valid = new[] { "GET", "POST", "PATCH", "PUT", "DELETE", "HEAD", "OPTIONS" };
            if (!valid.Contains(method.ToUpperInvariant()))
                return ($"Invalid HTTP method '{method}'. Use GET, POST, PATCH, PUT, DELETE, HEAD, or OPTIONS.", ExitCodes.InvalidArguments);
            httpMethod = new HttpMethod(method.ToUpperInvariant());
        }
        else if (body != null || bodyFile != null)
        {
            // No explicit --method but a body is present: default to POST (issue #1164).
            // An explicit --method always wins (handled by the branch above).
            httpMethod = HttpMethod.Post;
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

    /// <summary>
    /// Detects a `--path` value that Git Bash/MSYS rewrote from a leading-slash argument into an
    /// absolute Windows path before the CLI was exec'd (e.g. `/api/data/v9.2/contacts` becomes
    /// `C:/Program Files/Git/api/data/v9.2/contacts`). This is a shell artifact, not a user typo —
    /// the CLI never sees the leading slash. MSYS path translation always produces a drive-letter-
    /// rooted absolute path (`X:\` or `X:/`), so that prefix is a necessary and sufficient signature;
    /// matching on it alone avoids false positives on relative paths that merely contain a "git"
    /// segment. Exposed internal for unit testing of the MSYS hint (#1204).
    /// </summary>
    internal static bool LooksLikeShellMangledPath(string path)
        => path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '/' || path[2] == '\\');

    private static async Task<int> ExecuteAsync(
        string path,
        string? method,
        string? body,
        string? bodyFile,
        string[]? headers,
        bool include,
        string? environment,
        string? profile,
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

        // Resolve --body-file from disk (reads the file into the request body).
        var bodyResolution = await ResolveBodyAsync(body, bodyFile, cancellationToken);
        if (bodyResolution.Error != null)
        {
            Console.Error.WriteLine($"Error: {bodyResolution.Error}");
            return bodyResolution.ExitCode;
        }
        body = bodyResolution.Body;

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
            var envConfigService = serviceProvider.GetRequiredService<IEnvironmentConfigService>();
            var apiService = serviceProvider.GetRequiredService<IRawWebApiService>();

            var envConfig = await envConfigService.GetConfigAsync(connectionInfo.EnvironmentUrl, cancellationToken);
            var protectionLevel = ResolveProtectionLevel(envConfig?.Type ?? EnvironmentType.Unknown, envConfig?.Protection);

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

            // Write-guard blocks (exit 2) and HTTP response routing (exit 0/1) are handled
            // inside RunRequestAsync; only auth/connectivity failures reach the catch below.
            return await RunRequestAsync(apiService, request, include, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 3; // auth/connectivity failure per spec
        }
    }

    /// <summary>
    /// Resolves the effective request body: when --body-file is supplied, reads it from disk;
    /// otherwise returns the inline --body unchanged. On a missing file, returns an error + exit code.
    /// Exposed internal for unit testing of AC-08 (the --body-file read branch).
    /// </summary>
    internal static async Task<(string? Body, string? Error, int ExitCode)> ResolveBodyAsync(
        string? body,
        string? bodyFile,
        CancellationToken cancellationToken)
    {
        if (bodyFile == null)
            return (body, null, ExitCodes.Success);

        if (!File.Exists(bodyFile))
            return (null, $"Body file not found: '{bodyFile}'", ExitCodes.NotFoundError);

        var fileBody = await File.ReadAllTextAsync(bodyFile, cancellationToken);
        return (fileBody, null, ExitCodes.Success);
    }

    /// <summary>
    /// Resolves the effective protection level for the write guard.
    /// Fail-safe: an unknown/undetectable environment type resolves to Production so mutating
    /// requests are blocked without --confirm. (DmlSafetyGuard.DetectProtectionLevel maps Unknown
    /// to Development for SQL DML, which would fail open here — so api-request resolves Unknown locally.)
    /// An explicit per-environment protection override always wins.
    /// Exposed internal for unit testing of the write-guard fail-safe.
    /// </summary>
    internal static ProtectionLevel ResolveProtectionLevel(EnvironmentType envType, ProtectionLevel? configuredProtection)
        => WriteProtectionResolver.Resolve(envType, configuredProtection);

    /// <summary>
    /// Sends the request via the service and maps the response (or write-guard block) to an exit code.
    /// I1: success body → stdout; non-2xx response and write-block errors → stderr.
    /// Exposed internal for unit testing of AC-02 (exit codes) without live auth/network.
    /// </summary>
    internal static async Task<int> RunRequestAsync(
        IRawWebApiService apiService,
        RawWebApiRequest request,
        bool include,
        CancellationToken cancellationToken)
    {
        RawWebApiResponse response;
        try
        {
            response = await apiService.SendAsync(request, cancellationToken: cancellationToken);
        }
        catch (PpdsException ex) when (ex.ErrorCode == "Api.WriteBlocked")
        {
            Console.Error.WriteLine($"Error: {ex.UserMessage}");
            return ExitCodes.Failure; // 2 per spec
        }

        if (response.IsSuccess)
        {
            foreach (var line in FormatResponsePreamble(response, include))
                Console.WriteLine(line);
            if (!string.IsNullOrEmpty(response.Body))
                Console.WriteLine(response.Body);
            return ExitCodes.Success;
        }

        // Non-2xx: route entire response (preamble + body) to stderr so the
        // stream is consistent and piping to tools like jq works on success only.
        foreach (var line in FormatResponsePreamble(response, include))
            Console.Error.WriteLine(line);
        Console.Error.WriteLine(response.Body);
        return 1; // non-2xx per spec
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

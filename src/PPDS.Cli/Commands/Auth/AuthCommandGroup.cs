using System.CommandLine;
using System.Text.RegularExpressions;
using Microsoft.Crm.Sdk.Messages;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;

namespace PPDS.Cli.Commands.Auth;

/// <summary>
/// Authentication profile management commands.
/// </summary>
public static class AuthCommandGroup
{
    /// <summary>
    /// Pattern for valid profile names: starts and ends with letter/number, contains only letters, numbers, spaces, hyphens, or underscores.
    /// </summary>
    private static readonly Regex ProfileNamePattern = new(
        @"^[a-zA-Z0-9](?:[a-zA-Z0-9 _-]*[a-zA-Z0-9])?$",
        RegexOptions.Compiled);

    /// <summary>
    /// Creates the 'auth' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("auth", "Manage authentication profiles");

        command.Subcommands.Add(CreateCreateCommand());
        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateSelectCommand());
        command.Subcommands.Add(CreateDeleteCommand());
        command.Subcommands.Add(CreateUpdateCommand());
        command.Subcommands.Add(CreateNameCommand());
        command.Subcommands.Add(CreateClearCommand());
        command.Subcommands.Add(CreateWhoCommand());

        return command;
    }

    #region Create Command

    private static Command CreateCreateCommand()
    {
        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "The name you want to give to this authentication profile (maximum 30 characters)"
        };
        AddProfileNameValidator(nameOption);

        var environmentOption = new Option<string?>("--environment", "-env")
        {
            Description = "Default environment (ID, url, unique name, or partial name)"
        };

        var cloudOption = new Option<CloudEnvironment>("--cloud", "-ci")
        {
            Description = "Optional: The cloud instance to authenticate with",
            DefaultValueFactory = _ => CloudEnvironment.Public
        };

        var tenantOption = new Option<string?>("--tenant", "-t")
        {
            Description = "Tenant ID if using application ID/client secret or application ID/client certificate"
        };

        var deviceCodeOption = new Option<bool>("--deviceCode", "-dc")
        {
            Description = "Use the Microsoft Entra ID Device Code flow for interactive sign-in",
            DefaultValueFactory = _ => false
        };

        var applicationIdOption = new Option<string?>("--applicationId", "-id")
        {
            Description = "Optional: The application ID to authenticate with"
        };

        var clientSecretOption = new Option<string?>("--clientSecret", "-cs")
        {
            Description = "Optional: The client secret to authenticate with"
        };

        var certificatePathOption = new Option<string?>("--certificateDiskPath", "-cdp")
        {
            Description = "Optional: The certificate disk path to authenticate with"
        };

        var certificatePasswordOption = new Option<string?>("--certificatePassword", "-cp")
        {
            Description = "Optional: The certificate password to authenticate with"
        };

        var certificateThumbprintOption = new Option<string?>("--certificateThumbprint", "-ct")
        {
            Description = "Certificate thumbprint for Windows certificate store authentication"
        };

        var managedIdentityOption = new Option<bool>("--managedIdentity", "-mi")
        {
            Description = "Use Azure Managed Identity",
            DefaultValueFactory = _ => false
        };

        var usernameOption = new Option<string?>("--username", "-un")
        {
            Description = "Optional: The username to authenticate with; shows a Microsoft Entra ID dialog if not specified"
        };

        var passwordOption = new Option<string?>("--password", "-p")
        {
            Description = "Optional: The password to authenticate with"
        };

        var githubFederatedOption = new Option<bool>("--githubFederated", "-ghf")
        {
            Description = "Use GitHub Federation for Service Principal Auth; requires --tenant and --applicationId arguments",
            DefaultValueFactory = _ => false
        };

        var azureDevOpsFederatedOption = new Option<bool>("--azureDevOpsFederated", "-adof")
        {
            Description = "Use Azure DevOps Federation for Service Principal Auth; requires --tenant and --applicationId arguments",
            DefaultValueFactory = _ => false
        };

        var acceptCleartextOption = new Option<bool>("--accept-cleartext-caching")
        {
            Description = "Accept cleartext credential storage on Linux when secure storage (libsecret) is unavailable",
            DefaultValueFactory = _ => false,
            // Only show this option on Linux where it's relevant
            Hidden = !System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux)
        };

        var command = new Command("create", "Create and store authentication profiles on this computer")
        {
            nameOption,
            environmentOption,
            cloudOption,
            tenantOption,
            deviceCodeOption,
            applicationIdOption,
            clientSecretOption,
            certificatePathOption,
            certificatePasswordOption,
            certificateThumbprintOption,
            managedIdentityOption,
            usernameOption,
            passwordOption,
            githubFederatedOption,
            azureDevOpsFederatedOption,
            acceptCleartextOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new CreateOptions
            {
                Name = parseResult.GetValue(nameOption),
                Environment = parseResult.GetValue(environmentOption),
                Cloud = parseResult.GetValue(cloudOption),
                Tenant = parseResult.GetValue(tenantOption),
                DeviceCode = parseResult.GetValue(deviceCodeOption),
                ApplicationId = parseResult.GetValue(applicationIdOption),
                ClientSecret = parseResult.GetValue(clientSecretOption),
                CertificatePath = parseResult.GetValue(certificatePathOption),
                CertificatePassword = parseResult.GetValue(certificatePasswordOption),
                CertificateThumbprint = parseResult.GetValue(certificateThumbprintOption),
                ManagedIdentity = parseResult.GetValue(managedIdentityOption),
                Username = parseResult.GetValue(usernameOption),
                Password = parseResult.GetValue(passwordOption),
                GitHubFederated = parseResult.GetValue(githubFederatedOption),
                AzureDevOpsFederated = parseResult.GetValue(azureDevOpsFederatedOption),
                AcceptCleartextCaching = parseResult.GetValue(acceptCleartextOption)
            };

            return await ExecuteCreateAsync(options, cancellationToken);
        });

        return command;
    }

    private sealed class CreateOptions
    {
        public string? Name { get; set; }
        public string? Environment { get; set; }
        public CloudEnvironment Cloud { get; set; }
        public string? Tenant { get; set; }
        public bool DeviceCode { get; set; }
        public string? ApplicationId { get; set; }
        public string? ClientSecret { get; set; }
        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }
        public string? CertificateThumbprint { get; set; }
        public bool ManagedIdentity { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool GitHubFederated { get; set; }
        public bool AzureDevOpsFederated { get; set; }
        public bool AcceptCleartextCaching { get; set; }
    }

    private static async Task<int> ExecuteCreateAsync(CreateOptions options, CancellationToken cancellationToken)
    {
        // Check if secret is provided via environment variable (CI/automation scenario).
        // If so, skip secure credential store entirely - secrets will be provided at runtime.
        var envSpnSecret = Environment.GetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar);
        var bypassCredentialStore = !string.IsNullOrWhiteSpace(envSpnSecret);

        // Store secrets in secure credential store (not in profile), unless bypassed
        // Declared outside try block so finally can dispose it
        SecureCredentialStore? credentialStore = null;
        string? storedCredentialKey = null;

        try
        {
            var authMethod = DetermineAuthMethod(options);
            var validationError = ValidateAuthOptions(options, authMethod);
            if (validationError != null)
            {
                Console.Error.WriteLine($"Error: {validationError}");
                return ExitCodes.Failure;
            }

            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.Name) && collection.IsNameInUse(options.Name))
            {
                Console.Error.WriteLine($"Error: Profile name '{options.Name}' is already in use.");
                return ExitCodes.Failure;
            }

            var profile = new AuthProfile
            {
                Name = options.Name,
                AuthMethod = authMethod,
                Cloud = options.Cloud,
                TenantId = options.Tenant,
                ApplicationId = options.ApplicationId,
                CertificatePath = options.CertificatePath,
                CertificateThumbprint = options.CertificateThumbprint
            };

            if (!bypassCredentialStore)
            {
                credentialStore = new SecureCredentialStore(allowCleartextFallback: options.AcceptCleartextCaching);

                // Warn if using cleartext storage on Linux
                if (credentialStore.IsCleartextCachingEnabled)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine("Warning: Secure storage (libsecret) is unavailable. Credentials will be stored in cleartext.");
                    Console.Error.WriteLine("         Install libsecret and a keyring (gnome-keyring or kwallet) for secure storage.");
                    Console.ResetColor();
                }

                if (!string.IsNullOrWhiteSpace(options.ApplicationId))
                {
                    var storedCredential = new StoredCredential
                    {
                        ApplicationId = options.ApplicationId,
                        ClientSecret = options.ClientSecret,
                        CertificatePath = options.CertificatePath,
                        CertificatePassword = options.CertificatePassword
                    };
                    await credentialStore.StoreAsync(storedCredential, cancellationToken);
                    storedCredentialKey = options.ApplicationId;
                }
                else if (!string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.Password))
                {
                    // For username/password auth, key by username
                    var storedCredential = new StoredCredential
                    {
                        ApplicationId = options.Username,
                        Password = options.Password
                    };
                    await credentialStore.StoreAsync(storedCredential, cancellationToken);
                    storedCredentialKey = options.Username;
                }
            }

            // Helper to clean up stored credentials on failure
            async Task CleanupCredentialsAsync()
            {
                if (storedCredentialKey != null && credentialStore != null)
                {
                    await credentialStore.RemoveAsync(storedCredentialKey, cancellationToken);
                }
            }

            // For service principals, we must authenticate directly to the environment URL
            // Global discovery doesn't work with client credentials flow
            var isServicePrincipal = authMethod is AuthMethod.ClientSecret or AuthMethod.CertificateFile
                or AuthMethod.CertificateStore or AuthMethod.GitHubFederated or AuthMethod.AzureDevOpsFederated;

            string targetUrl;
            if (isServicePrincipal)
            {
                if (string.IsNullOrWhiteSpace(options.Environment))
                {
                    await CleanupCredentialsAsync();
                    ErrorOutput.WriteErrorLine($"--environment is required for {authMethod} authentication.");
                    ErrorOutput.WriteLine("Service principals must specify the full environment URL (e.g., https://org.crm.dynamics.com).");
                    return ExitCodes.Failure;
                }

                // Service principals must provide a full URL - they can't use global discovery
                // to resolve partial names like "Dev" or "Production"
                if (!options.Environment.Contains("://"))
                {
                    await CleanupCredentialsAsync();
                    ErrorOutput.WriteErrorLine("Service principals require a full environment URL.");
                    ErrorOutput.WriteLine($"  Provided: {options.Environment}");
                    ErrorOutput.WriteLine($"  Expected: https://org.crm.dynamics.com");
                    Console.Error.WriteLine();
                    ErrorOutput.WriteLine("Service principals cannot access Global Discovery to resolve environment names.");
                    ErrorOutput.WriteLine("Use the full Dataverse URL for the environment.");
                    return ExitCodes.Failure;
                }

                targetUrl = options.Environment;
            }
            else
            {
                // Interactive/device code can use global discovery
                targetUrl = "https://globaldisco.crm.dynamics.com";
            }

            Console.Error.WriteLine($"Authenticating with {authMethod}...");
            Console.Error.WriteLine();

            ICredentialProvider provider = authMethod switch
            {
                AuthMethod.InteractiveBrowser => new InteractiveBrowserCredentialProvider(options.Cloud, options.Tenant),
                AuthMethod.DeviceCode => new DeviceCodeCredentialProvider(options.Cloud, options.Tenant),
                AuthMethod.ClientSecret => new ClientSecretCredentialProvider(
                    options.ApplicationId!, options.ClientSecret!, options.Tenant!, options.Cloud),
                AuthMethod.CertificateFile => new CertificateFileCredentialProvider(
                    options.ApplicationId!, options.CertificatePath!, options.CertificatePassword, options.Tenant!, options.Cloud),
                AuthMethod.CertificateStore => new CertificateStoreCredentialProvider(
                    options.ApplicationId!, options.CertificateThumbprint!, options.Tenant!, cloud: options.Cloud),
                AuthMethod.ManagedIdentity => new ManagedIdentityCredentialProvider(options.ApplicationId),
                AuthMethod.GitHubFederated => new GitHubFederatedCredentialProvider(
                    options.ApplicationId!, options.Tenant!, options.Cloud),
                AuthMethod.AzureDevOpsFederated => new AzureDevOpsFederatedCredentialProvider(
                    options.ApplicationId!, options.Tenant!, options.Cloud),
                AuthMethod.UsernamePassword => new UsernamePasswordCredentialProvider(
                    options.Username!, options.Password!, options.Cloud, options.Tenant),
                _ => throw new NotSupportedException($"Auth method {authMethod} is not supported for profile creation.")
            };

            try
            {
                // forceInteractive=true ensures we always prompt, never reuse cached tokens
                var client = await provider.CreateServiceClientAsync(targetUrl, cancellationToken, forceInteractive: true);
                profile.Username = provider.Identity;
                profile.ObjectId = provider.ObjectId;
                profile.TokenExpiresOn = provider.TokenExpiresAt;
                profile.HomeAccountId = provider.HomeAccountId;

                // Store tenant ID from auth result if not already set
                if (string.IsNullOrEmpty(profile.TenantId) && !string.IsNullOrEmpty(provider.TenantId))
                {
                    profile.TenantId = provider.TenantId;
                }

                // Store the full authority URL
                if (!string.IsNullOrEmpty(profile.TenantId))
                {
                    profile.Authority = CloudEndpoints.GetAuthorityUrl(profile.Cloud, profile.TenantId);
                }

                var claims = JwtClaimsParser.Parse(provider.IdTokenClaims, provider.AccessToken);
                if (claims != null)
                {
                    profile.Puid = claims.Puid;
                }

                // Resolve environment if specified (must happen before client disposal)
                if (!string.IsNullOrWhiteSpace(options.Environment))
                {
                    if (isServicePrincipal)
                    {
                        // For service principals, get org info directly from the authenticated client
                        // Global discovery doesn't work with client credentials
                        profile.Environment = new EnvironmentInfo
                        {
                            Url = targetUrl.TrimEnd('/'),
                            DisplayName = client.ConnectedOrgFriendlyName ?? ExtractEnvironmentName(targetUrl),
                            UniqueName = client.ConnectedOrgUniqueName,
                            OrganizationId = client.ConnectedOrgId.ToString(),
                            EnvironmentId = client.EnvironmentId
                        };
                    }
                    else
                    {
                        // For interactive auth, use global discovery for richer environment info
                        Console.Error.WriteLine("Resolving environment...");
                        try
                        {
                            using var gds = new GlobalDiscoveryService(options.Cloud, options.Tenant);
                            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

                            DiscoveredEnvironment? resolved;
                            try
                            {
                                resolved = EnvironmentResolver.Resolve(environments, options.Environment);
                            }
                            catch (AmbiguousMatchException ex)
                            {
                                Console.Error.WriteLine($"Error: {ex.Message}");
                                return ExitCodes.Failure;
                            }

                            if (resolved == null)
                            {
                                Console.Error.WriteLine($"Error: Environment '{options.Environment}' not found.");
                                Console.Error.WriteLine();
                                Console.Error.WriteLine("Use 'ppds env list' to see available environments.");
                                return ExitCodes.Failure;
                            }

                            // Validate we can actually connect to this environment before saving
                            Console.Error.WriteLine($"Validating access to {resolved.FriendlyName}...");
                            try
                            {
                                // forceInteractive=false uses cached MSAL token from initial auth
                                using var validationClient = await provider.CreateServiceClientAsync(
                                    resolved.ApiUrl, cancellationToken, forceInteractive: false);
                                await validationClient.ExecuteAsync(new WhoAmIRequest(), cancellationToken);
                            }
                            catch (Exception validationEx)
                            {
                                Console.Error.WriteLine($"Error: Cannot access environment '{resolved.FriendlyName}': {validationEx.Message}");
                                Console.Error.WriteLine();
                                Console.Error.WriteLine("Profile was NOT created. Verify you have access to this environment.");
                                return ExitCodes.Failure;
                            }

                            profile.Environment = new EnvironmentInfo
                            {
                                Url = resolved.ApiUrl,
                                DisplayName = resolved.FriendlyName,
                                UniqueName = resolved.UniqueName,
                                EnvironmentId = resolved.EnvironmentId,
                                OrganizationId = resolved.Id.ToString(),
                                Type = resolved.EnvironmentType,
                                Region = resolved.Region
                            };
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Warning: Could not resolve environment: {ex.Message}");
                            Console.Error.WriteLine("Use 'ppds env select' after profile creation to set the environment.");
                        }
                    }
                }

                client.Dispose();
            }
            catch (AuthenticationException ex)
            {
                await CleanupCredentialsAsync();
                Console.Error.WriteLine($"Error: Authentication failed: {ex.Message}");
                return ExitCodes.Failure;
            }
            finally
            {
                provider.Dispose();
            }

            // Add to collection (auto-selects if first profile)
            collection.Add(profile);
            await store.SaveAsync(collection, cancellationToken);

            Console.Error.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Profile created: {profile.DisplayIdentifier}");
            Console.ResetColor();
            Console.Error.WriteLine($"  Auth: {profile.AuthMethod}");
            Console.Error.WriteLine($"  Identity: {profile.IdentityDisplay}");
            Console.Error.WriteLine($"  Cloud: {profile.Cloud}");
            if (profile.HasEnvironment)
            {
                Console.Error.WriteLine($"  Environment: {profile.Environment!.DisplayName}");
                Console.Error.WriteLine($"  Environment URL: {profile.Environment.Url}");
            }
            else
            {
                Console.Error.WriteLine($"  Environment: (none - use 'ppds env select' to set)");
            }

            if (collection.ActiveProfile?.Index == profile.Index)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("This profile is now active.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
        finally
        {
            credentialStore?.Dispose();
        }
    }

    private static AuthMethod DetermineAuthMethod(CreateOptions options)
    {
        if (options.GitHubFederated)
            return AuthMethod.GitHubFederated;

        if (options.AzureDevOpsFederated)
            return AuthMethod.AzureDevOpsFederated;

        if (options.ManagedIdentity)
            return AuthMethod.ManagedIdentity;

        if (!string.IsNullOrWhiteSpace(options.CertificateThumbprint))
            return AuthMethod.CertificateStore;

        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            return AuthMethod.CertificateFile;

        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
            return AuthMethod.ClientSecret;

        if (!string.IsNullOrWhiteSpace(options.Password))
            return AuthMethod.UsernamePassword;

        if (options.DeviceCode)
            return AuthMethod.DeviceCode;

        return InteractiveBrowserCredentialProvider.IsAvailable()
            ? AuthMethod.InteractiveBrowser
            : AuthMethod.DeviceCode;
    }

    private static string? ValidateAuthOptions(CreateOptions options, AuthMethod authMethod)
    {
        return authMethod switch
        {
            AuthMethod.InteractiveBrowser => null, // No required options
            AuthMethod.DeviceCode => null, // No required options

            AuthMethod.ClientSecret => ValidateClientSecret(options),

            AuthMethod.CertificateFile => ValidateCertificateFile(options),

            AuthMethod.CertificateStore => ValidateCertificateStore(options),

            AuthMethod.ManagedIdentity => null, // ApplicationId is optional (for user-assigned)

            AuthMethod.GitHubFederated => ValidateFederated(options, "GitHub"),

            AuthMethod.AzureDevOpsFederated => ValidateFederated(options, "Azure DevOps"),

            AuthMethod.UsernamePassword => ValidateUsernamePassword(options),

            _ => $"Auth method {authMethod} is not supported."
        };
    }

    private static string? ValidateClientSecret(CreateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationId))
            return "--applicationId is required for client secret authentication.";
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            return "--clientSecret is required for client secret authentication.";
        if (string.IsNullOrWhiteSpace(options.Tenant))
            return "--tenant is required for client secret authentication.";
        return null;
    }

    private static string? ValidateCertificateFile(CreateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationId))
            return "--applicationId is required for certificate authentication.";
        if (string.IsNullOrWhiteSpace(options.CertificatePath))
            return "--certificateDiskPath is required for certificate file authentication.";
        if (string.IsNullOrWhiteSpace(options.Tenant))
            return "--tenant is required for certificate authentication.";
        if (!System.IO.File.Exists(options.CertificatePath))
            return $"Certificate file not found: {options.CertificatePath}";
        return null;
    }

    private static string? ValidateCertificateStore(CreateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationId))
            return "--applicationId is required for certificate authentication.";
        if (string.IsNullOrWhiteSpace(options.CertificateThumbprint))
            return "--certificateThumbprint is required for certificate store authentication.";
        if (string.IsNullOrWhiteSpace(options.Tenant))
            return "--tenant is required for certificate authentication.";
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return "Certificate store authentication is only supported on Windows. Use --certificateDiskPath instead.";
        return null;
    }

    private static string? ValidateFederated(CreateOptions options, string federationType)
    {
        if (string.IsNullOrWhiteSpace(options.ApplicationId))
            return $"--applicationId is required for {federationType} federated authentication.";
        if (string.IsNullOrWhiteSpace(options.Tenant))
            return $"--tenant is required for {federationType} federated authentication.";
        return null;
    }

    private static string? ValidateUsernamePassword(CreateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Username))
            return "--username is required for username/password authentication.";
        if (string.IsNullOrWhiteSpace(options.Password))
            return "--password is required for username/password authentication.";
        return null;
    }

    #endregion

    #region List Command

    private static Command CreateListCommand()
    {
        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format",
            DefaultValueFactory = _ => OutputFormat.Text
        };

        var command = new Command("list", "List all authentication profiles")
        {
            outputFormatOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputFormat = parseResult.GetValue(outputFormatOption);
            return await ExecuteListAsync(outputFormat, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteListAsync(OutputFormat outputFormat, CancellationToken cancellationToken)
    {
        try
        {
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            if (outputFormat == OutputFormat.Json)
            {
                WriteProfilesAsJson(collection);
            }
            else
            {
                WriteProfilesAsText(collection);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static void WriteProfilesAsText(ProfileCollection collection)
    {
        if (collection.Count == 0)
        {
            Console.Error.WriteLine("No profiles configured.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Use 'ppds auth create' to create a profile.");
            return;
        }

        var rows = collection.All.Select(p => new
        {
            Index = $"[{p.Index}]",
            Active = collection.ActiveProfile?.Index == p.Index ? "*" : "",
            Method = p.AuthMethod.ToString(),
            Name = p.Name ?? "",
            User = p.IdentityDisplay,
            Cloud = p.Cloud.ToString(),
            Environment = p.Environment?.DisplayName ?? "",
            EnvironmentUrl = p.Environment?.Url ?? ""
        }).ToList();

        var colIndex = Math.Max(5, rows.Max(r => r.Index.Length));
        var colActive = 6;
        var colMethod = Math.Max(6, rows.Max(r => r.Method.Length));
        var colName = Math.Max(4, rows.Max(r => r.Name.Length));
        var colUser = Math.Max(4, rows.Max(r => r.User.Length));
        var colCloud = Math.Max(5, rows.Max(r => r.Cloud.Length));
        var colEnv = Math.Max(11, rows.Max(r => r.Environment.Length));

        Console.WriteLine(
            $"{"Index".PadRight(colIndex)} " +
            $"{"Active".PadRight(colActive)} " +
            $"{"Method".PadRight(colMethod)} " +
            $"{"Name".PadRight(colName)} " +
            $"{"User".PadRight(colUser)} " +
            $"{"Cloud".PadRight(colCloud)} " +
            $"{"Environment".PadRight(colEnv)} " +
            "Environment Url");

        foreach (var row in rows)
        {
            var isActive = row.Active == "*";
            if (isActive) Console.ForegroundColor = ConsoleColor.Green;

            Console.WriteLine(
                $"{row.Index.PadRight(colIndex)} " +
                $"{row.Active.PadRight(colActive)} " +
                $"{row.Method.PadRight(colMethod)} " +
                $"{row.Name.PadRight(colName)} " +
                $"{row.User.PadRight(colUser)} " +
                $"{row.Cloud.PadRight(colCloud)} " +
                $"{row.Environment.PadRight(colEnv)} " +
                row.EnvironmentUrl);

            if (isActive) Console.ResetColor();
        }
    }

    private static void WriteProfilesAsJson(ProfileCollection collection)
    {
        var output = new
        {
            activeProfile = collection.ActiveProfileName,
            profiles = collection.All.Select(p => new
            {
                index = p.Index,
                name = p.Name,
                identity = p.IdentityDisplay,
                authMethod = p.AuthMethod.ToString(),
                cloud = p.Cloud.ToString(),
                environment = p.Environment != null ? new
                {
                    url = p.Environment.Url,
                    displayName = p.Environment.DisplayName
                } : null,
                isActive = collection.ActiveProfile?.Index == p.Index,
                createdAt = p.CreatedAt,
                lastUsedAt = p.LastUsedAt
            })
        };

        var jsonOutput = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        Console.WriteLine(jsonOutput);
    }

    #endregion

    #region Select Command

    private static Command CreateSelectCommand()
    {
        var indexOption = new Option<int?>("--index", "-i")
        {
            Description = "The index of the profile to be active"
        };

        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "The name of the profile to be active"
        };

        var command = new Command("select", "Select which authentication profile should be active")
        {
            indexOption,
            nameOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var index = parseResult.GetValue(indexOption);
            var name = parseResult.GetValue(nameOption);
            return await ExecuteSelectAsync(index, name, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteSelectAsync(int? index, string? name, CancellationToken cancellationToken)
    {
        try
        {
            if (index == null && string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Error: Must provide either --index or --name.");
                return ExitCodes.Failure;
            }

            if (index != null && !string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Error: Must provide either --index or --name but not both.");
                return ExitCodes.Failure;
            }

            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            AuthProfile? profile;
            if (index != null)
            {
                profile = collection.GetByIndex(index.Value);
                if (profile == null)
                {
                    Console.Error.WriteLine($"Error: Profile with index {index} not found.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Use 'ppds auth list' to see available profiles.");
                    return ExitCodes.Failure;
                }
            }
            else
            {
                profile = collection.GetByName(name!);
                if (profile == null)
                {
                    Console.Error.WriteLine($"Error: Profile '{name}' not found.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Use 'ppds auth list' to see available profiles.");
                    return ExitCodes.Failure;
                }
            }

            collection.SetActiveByIndex(profile.Index);
            await store.SaveAsync(collection, cancellationToken);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Active profile: {profile.DisplayIdentifier}");
            Console.ResetColor();
            Console.Error.WriteLine($"  Identity: {profile.IdentityDisplay}");
            if (profile.HasEnvironment)
            {
                Console.Error.WriteLine($"  Environment: {profile.Environment!.DisplayName}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Delete Command

    private static Command CreateDeleteCommand()
    {
        var indexOption = new Option<int?>("--index", "-i")
        {
            Description = "The index of the profile to be deleted"
        };

        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "The name of the profile to be deleted"
        };

        var command = new Command("delete", "Delete a particular authentication profile")
        {
            indexOption,
            nameOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var index = parseResult.GetValue(indexOption);
            var name = parseResult.GetValue(nameOption);
            return await ExecuteDeleteAsync(index, name, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteDeleteAsync(int? index, string? name, CancellationToken cancellationToken)
    {
        try
        {
            if (index == null && string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Error: Must provide either --index or --name.");
                return ExitCodes.Failure;
            }

            if (index != null && !string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Error: Must provide either --index or --name but not both.");
                return ExitCodes.Failure;
            }

            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            AuthProfile? profile;
            if (index != null)
            {
                profile = collection.GetByIndex(index.Value);
                if (profile == null)
                {
                    Console.Error.WriteLine($"Error: Profile with index {index} not found.");
                    return ExitCodes.Failure;
                }
            }
            else
            {
                profile = collection.GetByName(name!);
                if (profile == null)
                {
                    Console.Error.WriteLine($"Error: Profile '{name}' not found.");
                    return ExitCodes.Failure;
                }
            }

            // Clean up stored credentials for this profile (only if no other profiles share them)
            string? credentialKey = null;
            if (!string.IsNullOrWhiteSpace(profile.ApplicationId))
            {
                credentialKey = profile.ApplicationId;
            }
            else if (profile.AuthMethod == AuthMethod.UsernamePassword && !string.IsNullOrWhiteSpace(profile.Username))
            {
                // For username/password auth, credentials are keyed by username
                credentialKey = profile.Username;
            }

            if (credentialKey != null)
            {
                // Check if any other profiles share this credential key
                var isShared = collection.All
                    .Where(p => p.Index != profile.Index)
                    .Any(p =>
                    {
                        if (!string.IsNullOrWhiteSpace(p.ApplicationId))
                            return string.Equals(credentialKey, p.ApplicationId, StringComparison.OrdinalIgnoreCase);
                        if (p.AuthMethod == AuthMethod.UsernamePassword && !string.IsNullOrWhiteSpace(p.Username))
                            return string.Equals(credentialKey, p.Username, StringComparison.OrdinalIgnoreCase);
                        return false;
                    });

                if (!isShared)
                {
                    using var credentialStore = new SecureCredentialStore();
                    await credentialStore.RemoveAsync(credentialKey, cancellationToken);
                }
            }

            collection.RemoveByIndex(profile.Index);
            await store.SaveAsync(collection, cancellationToken);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Profile deleted: {profile.DisplayIdentifier}");
            Console.ResetColor();

            if (collection.ActiveProfile != null)
            {
                Console.Error.WriteLine($"Active profile is now: {collection.ActiveProfile.DisplayIdentifier}");
            }
            else if (collection.Count > 0)
            {
                Console.Error.WriteLine("No active profile. Use 'ppds auth select' to set one.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Update Command

    private static Command CreateUpdateCommand()
    {
        var indexOption = new Option<int>("--index", "-i")
        {
            Description = "The index of the profile to update",
            Required = true
        };

        var nameOption = new Option<string?>("--name", "-n")
        {
            Description = "The name to give this profile (max 30 characters)"
        };
        AddProfileNameValidator(nameOption);

        var envOption = new Option<string?>("--environment", "-env")
        {
            Description = "Default environment (ID, URL, unique name, or partial name)"
        };

        var command = new Command("update", "Update profile name or default environment")
        {
            indexOption,
            nameOption,
            envOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var index = parseResult.GetValue(indexOption);
            var name = parseResult.GetValue(nameOption);
            var env = parseResult.GetValue(envOption);
            return await ExecuteUpdateAsync(index, name, env, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteUpdateAsync(int index, string? newName, string? newEnvironment, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName) && string.IsNullOrWhiteSpace(newEnvironment))
            {
                Console.Error.WriteLine("Error: At least one update option (--name or --environment) must be specified.");
                return ExitCodes.Failure;
            }

            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.GetByIndex(index);
            if (profile == null)
            {
                Console.Error.WriteLine($"Error: Profile with index {index} not found.");
                return ExitCodes.Failure;
            }

            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (collection.IsNameInUse(newName, profile.Index))
                {
                    Console.Error.WriteLine($"Error: Profile name '{newName}' is already in use.");
                    return ExitCodes.Failure;
                }
                var oldName = profile.DisplayIdentifier;
                profile.Name = newName;
                Console.Error.WriteLine($"Name updated: {oldName} -> {profile.DisplayIdentifier}");
            }

            if (!string.IsNullOrWhiteSpace(newEnvironment))
            {
                Console.Error.WriteLine($"Resolving environment '{newEnvironment}'...");

                // Use multi-layer resolution: direct connection first for URLs, Global Discovery for names
                using var credentialStore = new SecureCredentialStore();
                using var resolver = new EnvironmentResolutionService(profile, credentialStore: credentialStore);
                var result = await resolver.ResolveAsync(newEnvironment, cancellationToken);

                if (!result.Success)
                {
                    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
                    return ExitCodes.Failure;
                }

                profile.Environment = result.Environment;

                var methodNote = result.Method == ResolutionMethod.DirectConnection
                    ? " (via direct connection)"
                    : " (via Global Discovery)";
                Console.Error.WriteLine($"Environment set: {result.Environment!.DisplayName}{methodNote}");
            }

            await store.SaveAsync(collection, cancellationToken);

            Console.Error.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Profile updated: {profile.DisplayIdentifier}");
            Console.ResetColor();

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Name Command

    private static Command CreateNameCommand()
    {
        var indexOption = new Option<int>("--index", "-i")
        {
            Description = "The index of the profile to be named/renamed",
            Required = true
        };

        var nameOption = new Option<string>("--name", "-n")
        {
            Description = "The name you want to give to this authentication profile (maximum 30 characters)",
            Required = true
        };
        nameOption.Validators.Add(result =>
        {
            var name = result.GetValue(nameOption);
            if (string.IsNullOrEmpty(name)) return;

            if (name.Length > 30)
            {
                result.AddError("Profile name cannot exceed 30 characters");
            }
            else if (!ProfileNamePattern.IsMatch(name))
            {
                result.AddError("Profile name must start with a letter or number and contain only letters, numbers, spaces, hyphens, or underscores");
            }
        });

        var command = new Command("name", "Name or rename an existing authentication profile")
        {
            indexOption,
            nameOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var index = parseResult.GetValue(indexOption);
            var name = parseResult.GetValue(nameOption)!;
            return await ExecuteNameAsync(index, name, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteNameAsync(int index, string newName, CancellationToken cancellationToken)
    {
        try
        {
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.GetByIndex(index);
            if (profile == null)
            {
                Console.Error.WriteLine($"Error: Profile with index {index} not found.");
                return ExitCodes.Failure;
            }

            if (collection.IsNameInUse(newName, profile.Index))
            {
                Console.Error.WriteLine($"Error: Profile name '{newName}' is already in use.");
                return ExitCodes.Failure;
            }

            var oldName = profile.DisplayIdentifier;
            profile.Name = newName;
            await store.SaveAsync(collection, cancellationToken);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Profile renamed: {oldName} -> {profile.DisplayIdentifier}");
            Console.ResetColor();

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Clear Command

    private static Command CreateClearCommand()
    {
        var command = new Command("clear", "Delete all profiles and cached credentials");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            return await ExecuteClearAsync(cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            if (collection.Count == 0)
            {
                Console.Error.WriteLine("No profiles to clear.");
                return ExitCodes.Success;
            }

            store.Delete();

            // Clear all MSAL token caches (file + platform-specific secure storage)
            await TokenCacheManager.ClearAllCachesAsync();

            // Clear secure credential store
            using var credentialStore = new SecureCredentialStore();
            await credentialStore.ClearAsync(cancellationToken);

            Console.Error.WriteLine("Authentication profiles, token cache, and stored credentials removed");

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Who Command

    private static Command CreateWhoCommand()
    {
        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format",
            DefaultValueFactory = _ => OutputFormat.Text
        };

        var command = new Command("who", "Show the current active profile")
        {
            outputFormatOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputFormat = parseResult.GetValue(outputFormatOption);
            return await ExecuteWhoAsync(outputFormat, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteWhoAsync(OutputFormat outputFormat, CancellationToken cancellationToken)
    {
        try
        {
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                if (outputFormat == OutputFormat.Json)
                {
                    Console.WriteLine("{\"active\": null}");
                }
                else
                {
                    Console.Error.WriteLine("No active profile.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Use 'ppds auth create' to create a profile.");
                }
                return ExitCodes.Success;
            }

            // Get token cache type
            var cacheType = TokenCacheDetector.GetCacheType();

            if (outputFormat == OutputFormat.Json)
            {
                // Determine token status
                string? tokenStatus = null;
                if (profile.TokenExpiresOn.HasValue)
                {
                    tokenStatus = profile.TokenExpiresOn.Value < DateTimeOffset.UtcNow ? "expired" : "valid";
                }

                var output = new
                {
                    active = new
                    {
                        index = profile.Index,
                        name = profile.Name,
                        method = profile.AuthMethod.ToString(),
                        type = cacheType.ToString(),
                        cloud = profile.Cloud.ToString(),
                        tenantId = profile.TenantId,
                        user = profile.Username,
                        objectId = profile.ObjectId,
                        puid = profile.Puid,
                        applicationId = profile.ApplicationId,
                        tokenExpires = profile.TokenExpiresOn,
                        tokenStatus,
                        authority = profile.Authority ?? CloudEndpoints.GetAuthorityUrl(profile.Cloud, profile.TenantId),
                        environment = profile.Environment != null ? new
                        {
                            region = profile.Environment.Region,
                            environmentId = profile.Environment.EnvironmentId,
                            environmentType = profile.Environment.Type,
                            organizationId = profile.Environment.OrganizationId,
                            uniqueName = profile.Environment.UniqueName,
                            displayName = profile.Environment.DisplayName,
                            url = profile.Environment.Url
                        } : null,
                        createdAt = profile.CreatedAt,
                        lastUsedAt = profile.LastUsedAt
                    }
                };

                var jsonOutput = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                Console.WriteLine(jsonOutput);
            }
            else
            {
                var identity = !string.IsNullOrEmpty(profile.Username)
                    ? profile.Username
                    : !string.IsNullOrEmpty(profile.ApplicationId)
                        ? $"app:{profile.ApplicationId}"
                        : "(unknown)";

                Console.WriteLine($"Connected as {identity}");
                Console.WriteLine();

                // Auth info section - ordered to match PAC CLI
                Console.WriteLine($"Method:                      {profile.AuthMethod}");
                Console.WriteLine($"Type:                        {cacheType}");
                Console.WriteLine($"Cloud:                       {profile.Cloud}");

                if (!string.IsNullOrEmpty(profile.TenantId))
                {
                    Console.WriteLine($"Tenant Id:                   {profile.TenantId}");
                }

                if (!string.IsNullOrEmpty(profile.Username))
                {
                    Console.WriteLine($"User:                        {profile.Username}");
                }

                if (!string.IsNullOrEmpty(profile.ObjectId))
                {
                    Console.WriteLine($"Entra ID Object Id:          {profile.ObjectId}");
                }

                if (!string.IsNullOrEmpty(profile.Puid))
                {
                    Console.WriteLine($"PUID:                        {profile.Puid}");
                }

                if (!string.IsNullOrEmpty(profile.ApplicationId))
                {
                    Console.WriteLine($"Application Id:              {profile.ApplicationId}");
                }

                if (profile.TokenExpiresOn.HasValue)
                {
                    var isExpired = profile.TokenExpiresOn.Value < DateTimeOffset.UtcNow;
                    if (isExpired)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Token Expires:               {profile.TokenExpiresOn.Value:yyyy-MM-dd HH:mm:ss zzz} (EXPIRED)");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine($"Token Expires:               {profile.TokenExpiresOn.Value:yyyy-MM-dd HH:mm:ss zzz}");
                    }
                }

                // Show authority based on cloud
                var authority = profile.Authority ?? CloudEndpoints.GetAuthorityUrl(profile.Cloud, profile.TenantId);
                Console.WriteLine($"Authority:                   {authority}");

                // Environment section - ordered to match PAC CLI
                if (profile.HasEnvironment)
                {
                    if (!string.IsNullOrEmpty(profile.Environment!.Region))
                    {
                        Console.WriteLine($"Environment Geo:             {profile.Environment.Region}");
                    }

                    if (!string.IsNullOrEmpty(profile.Environment.EnvironmentId))
                    {
                        Console.WriteLine($"Environment Id:              {profile.Environment.EnvironmentId}");
                    }

                    if (!string.IsNullOrEmpty(profile.Environment.Type))
                    {
                        Console.WriteLine($"Environment Type:            {profile.Environment.Type}");
                    }

                    if (!string.IsNullOrEmpty(profile.Environment.OrganizationId))
                    {
                        Console.WriteLine($"Organization Id:             {profile.Environment.OrganizationId}");
                    }

                    if (!string.IsNullOrEmpty(profile.Environment.UniqueName))
                    {
                        Console.WriteLine($"Organization Unique Name:    {profile.Environment.UniqueName}");
                    }

                    if (!string.IsNullOrEmpty(profile.Environment.DisplayName))
                    {
                        Console.WriteLine($"Organization Friendly Name:  {profile.Environment.DisplayName}");
                    }
                }
                else
                {
                    Console.Error.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine("No environment selected.");
                    Console.ResetColor();
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Use 'ppds env select' to set an environment.");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Adds standard profile name validation to an option.
    /// </summary>
    private static void AddProfileNameValidator(Option<string?> nameOption)
    {
        nameOption.Validators.Add(result =>
        {
            var name = result.GetValue(nameOption);
            if (string.IsNullOrEmpty(name)) return;

            if (name.Length > 30)
            {
                result.AddError("Profile name cannot exceed 30 characters");
            }
            else if (!ProfileNamePattern.IsMatch(name))
            {
                result.AddError("Profile name must start with a letter or number and contain only letters, numbers, spaces, hyphens, or underscores");
            }
        });
    }

    private static string ExtractEnvironmentName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        // Extract org name from host (e.g., "myorg" from "myorg.crm.dynamics.com")
        var parts = uri.Host.Split('.');
        return parts.Length > 0 ? parts[0] : url;
    }

    #endregion
}

using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Diagnostics;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Authoring = PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Security;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using System.Threading;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;
using PluginAssemblyInfoModel = PPDS.Cli.Plugins.Registration.PluginAssemblyInfo;
using PluginPackageInfoModel = PPDS.Cli.Plugins.Registration.PluginPackageInfo;
using PluginStepInfoModel = PPDS.Cli.Plugins.Registration.PluginStepInfo;
using ConnRefRelationshipType = PPDS.Cli.Services.ConnectionReferences.RelationshipType;
using WebResourceInfoModel = PPDS.Cli.Services.WebResources.WebResourceInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

public partial class RpcMethodHandler
{
    #region Profile Invalidation

    /// <summary>
    /// Invalidates cached pools that use the specified profile.
    /// Called by VS Code extension after auth profile changes.
    /// </summary>
    [JsonRpcMethod("profiles/invalidate")]
    public ProfilesInvalidateResponse ProfilesInvalidate(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'profileName' parameter is required");
        }

        _poolManager.InvalidateProfile(profileName);

        return new ProfilesInvalidateResponse
        {
            ProfileName = profileName,
            Invalidated = true
        };
    }

    #endregion

    #region Profile CRUD

    /// <summary>
    /// Creates a new authentication profile.
    /// Maps to: ppds auth create --json
    /// </summary>
    /// <remarks>
    /// SECURITY: <paramref name="clientSecret"/>, <paramref name="password"/>, and
    /// <paramref name="certificatePassword"/> are transmitted as plain-text JSON-RPC parameters
    /// over the local stdio pipe. This is acceptable for local-only IPC (the pipe is not
    /// network-accessible) but these values MUST NOT be logged anywhere in the call stack.
    /// StreamJsonRpc trace logging is deliberately not configured in ServeCommand to avoid
    /// capturing sensitive parameters. The extension should prefer device code or browser-based
    /// auth flows (Interactive, DeviceCode) over secret-bearing flows when possible.
    /// </remarks>
    [JsonRpcMethod("profiles/create")]
    public async Task<ProfileCreateResponse> ProfilesCreateAsync(
        string authMethod,
        string? name = null,
        string? applicationId = null,
        [SensitiveData("Client secret transmitted as plain-text over local stdio pipe")]
        string? clientSecret = null,
        string? tenantId = null,
        string? environmentUrl = null,
        string? certificatePath = null,
        [SensitiveData("Certificate password transmitted as plain-text over local stdio pipe")]
        string? certificatePassword = null,
        string? certificateThumbprint = null,
        string? username = null,
        [SensitiveData("Password transmitted as plain-text over local stdio pipe")]
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authMethod))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'authMethod' parameter is required");
        }

        if (!Enum.TryParse<AuthMethod>(authMethod, ignoreCase: true, out var parsedMethod))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"Invalid auth method '{authMethod}'. Valid values: {string.Join(", ", Enum.GetNames<AuthMethod>())}");
        }

        var request = new ProfileCreateRequest
        {
            Name = name,
            AuthMethod = parsedMethod,
            ApplicationId = applicationId,
            ClientSecret = clientSecret,
            TenantId = tenantId,
            Environment = environmentUrl,
            CertificatePath = certificatePath,
            CertificatePassword = certificatePassword,
            CertificateThumbprint = certificateThumbprint,
            Username = username,
            Password = password,
        };

        try
        {
            var profileService = _authServices.GetRequiredService<IProfileService>();
            var result = await profileService.CreateProfileAsync(
                request,
                deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc, name),
                beforeInteractiveAuth: null,
                cancellationToken: cancellationToken);

            return new ProfileCreateResponse
            {
                Index = result.Index,
                Name = result.Name,
                Identity = result.Identity,
                AuthMethod = result.AuthMethod.ToString(),
                Environment = result.EnvironmentUrl,
            };
        }
        catch (PpdsException ex)
        {
            throw MapPpdsToRpcException(ex);
        }
    }

    /// <summary>
    /// Deletes an authentication profile by index or name.
    /// Maps to: ppds auth delete --json
    /// </summary>
    [JsonRpcMethod("profiles/delete")]
    public async Task<ProfileDeleteResponse> ProfilesDeleteAsync(
        int? index = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (index == null && string.IsNullOrWhiteSpace(name))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "Either 'index' or 'name' parameter is required");
        }

        if (index != null && !string.IsNullOrWhiteSpace(name))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                "Provide either 'index' or 'name', not both");
        }

        var nameOrIndex = index != null ? index.Value.ToString() : name!;
        try
        {
            var profileService = _authServices.GetRequiredService<IProfileService>();
            var deleted = await profileService.DeleteProfileAsync(nameOrIndex, cancellationToken);

            return new ProfileDeleteResponse
            {
                Deleted = deleted,
                ProfileName = nameOrIndex,
            };
        }
        catch (PpdsException ex)
        {
            throw MapPpdsToRpcException(ex);
        }
    }

    /// <summary>
    /// Renames an authentication profile.
    /// Maps to: ppds auth rename --json
    /// </summary>
    [JsonRpcMethod("profiles/rename")]
    public async Task<ProfileRenameResponse> ProfilesRenameAsync(
        string currentName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'currentName' parameter is required");
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'newName' parameter is required");
        }

        try
        {
            var profileService = _authServices.GetRequiredService<IProfileService>();
            var result = await profileService.UpdateProfileAsync(
                currentName,
                newName: newName,
                cancellationToken: cancellationToken);

            return new ProfileRenameResponse
            {
                Index = result.Index,
                PreviousName = currentName,
                NewName = result.Name ?? newName,
            };
        }
        catch (PpdsException ex)
        {
            throw MapPpdsToRpcException(ex);
        }
    }

    #endregion
}

/// <summary>
/// Response for profiles/invalidate method.
/// </summary>
public class ProfilesInvalidateResponse
{
    /// <summary>
    /// Gets or sets the profile name that was invalidated.
    /// </summary>
    [JsonPropertyName("profileName")]
    public string ProfileName { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether invalidation was successful.
    /// </summary>
    [JsonPropertyName("invalidated")]
    public bool Invalidated { get; set; }
}

/// <summary>
/// Response for profiles/create method.
/// </summary>
public class ProfileCreateResponse
{
    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("identity")] public string Identity { get; set; } = "";

    [JsonPropertyName("authMethod")] public string AuthMethod { get; set; } = "";

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Environment { get; set; }
}

/// <summary>
/// Response for profiles/delete method.
/// </summary>
public class ProfileDeleteResponse
{
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }

    [JsonPropertyName("profileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfileName { get; set; }
}

/// <summary>
/// Response for profiles/rename method.
/// </summary>
public class ProfileRenameResponse
{
    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("previousName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousName { get; set; }

    [JsonPropertyName("newName")] public string NewName { get; set; } = "";
}

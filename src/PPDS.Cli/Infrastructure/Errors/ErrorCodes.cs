namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// Hierarchical error codes for structured error reporting.
/// Format: Category.Subcategory (e.g., "Auth.ProfileNotFound").
/// </summary>
/// <remarks>
/// <para>
/// These codes are designed for programmatic error handling by consumers
/// (VS Code extension, scripts, CI/CD pipelines). Use the hierarchical
/// format to enable both specific and category-level error matching.
/// </para>
/// <para>
/// <b>Category conventions:</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <term><see cref="Auth"/> / <see cref="Profile"/></term>
/// <description>Authentication, authorization, and profile management
/// failures (token expiry, missing credentials, unknown profile).</description>
/// </item>
/// <item>
/// <term><see cref="Connection"/></term>
/// <description>Dataverse connectivity problems (network, throttling,
/// environment discovery, URL validation).</description>
/// </item>
/// <item>
/// <term><see cref="Validation"/></term>
/// <description>User-input validation — missing required fields, invalid
/// values, schema violations, disallowed paths or URL schemes.</description>
/// </item>
/// <item>
/// <term><see cref="Operation"/></term>
/// <description>Generic operation outcomes — not found, duplicate,
/// dependency missing, cancelled, timeout, partial failure.</description>
/// </item>
/// <item>
/// <term><see cref="Query"/></term>
/// <description>Query engine errors (SQL parsing, FetchXML, TDS endpoint,
/// DML safety guards, aggregation limits).</description>
/// </item>
/// <item>
/// <term><see cref="External"/> / <see cref="UpdateCheck"/></term>
/// <description>Failures in out-of-process calls to external services
/// (GitHub API, NuGet, <c>dotnet tool update</c>).</description>
/// </item>
/// <item>
/// <term>Domain categories (<see cref="Solution"/>, <see cref="Plugin"/>,
/// <see cref="ServiceEndpoint"/>, <see cref="CustomApi"/>,
/// <see cref="DataProvider"/>, <see cref="DataSource"/>,
/// <see cref="MetadataAuthoring"/>, <see cref="WebResource"/>)</term>
/// <description>Feature-specific validation/not-found/conflict errors.
/// Prefer a domain category when the failure is tightly coupled to a
/// specific Dataverse entity type.</description>
/// </item>
/// </list>
/// <para>
/// <b>Cross-project bridge (deferred to v1.1):</b>
/// <see cref="PPDS.Cli"/> defines these codes but <see cref="PPDS.Dataverse"/>
/// uses its own enum <c>BulkOperationErrorCode</c> for bulk-op failures. For
/// v1 they remain independent: <c>PPDS.Dataverse</c> throws its own exceptions
/// with <c>BulkOperationErrorCode</c> values, and service-layer code wraps
/// those in <see cref="PpdsException"/> using <see cref="Operation.PartialFailure"/>
/// or a more specific code as appropriate. v1.1 will bridge the two via a
/// static translation layer so callers see a single <see cref="ErrorCodes"/>
/// surface regardless of which project raised the error.
/// </para>
/// </remarks>
public static class ErrorCodes
{
    /// <summary>
    /// Profile management errors.
    /// </summary>
    public static class Profile
    {
        /// <summary>The requested profile was not found.</summary>
        public const string NotFound = "Profile.NotFound";

        /// <summary>No active profile is configured.</summary>
        public const string NoActiveProfile = "Profile.NoActiveProfile";

        /// <summary>Profile name is already in use.</summary>
        public const string NameInUse = "Profile.NameInUse";

        /// <summary>Profile name is invalid.</summary>
        public const string InvalidName = "Profile.InvalidName";
    }

    /// <summary>
    /// Authentication-related errors.
    /// </summary>
    public static class Auth
    {
        /// <summary>The requested profile was not found.</summary>
        public const string ProfileNotFound = "Auth.ProfileNotFound";

        /// <summary>Authentication token has expired.</summary>
        public const string Expired = "Auth.Expired";

        /// <summary>Invalid credentials were provided.</summary>
        public const string InvalidCredentials = "Auth.InvalidCredentials";

        /// <summary>User lacks required permissions.</summary>
        public const string InsufficientPermissions = "Auth.InsufficientPermissions";

        /// <summary>No active profile is configured.</summary>
        public const string NoActiveProfile = "Auth.NoActiveProfile";

        /// <summary>Profile name is already in use.</summary>
        public const string ProfileExists = "Auth.ProfileExists";

        /// <summary>Certificate file not found or invalid.</summary>
        public const string CertificateError = "Auth.CertificateError";
    }

    /// <summary>
    /// Connection-related errors.
    /// </summary>
    public static class Connection
    {
        /// <summary>Failed to establish connection.</summary>
        public const string Failed = "Connection.Failed";

        /// <summary>Request was throttled by service protection limits.</summary>
        public const string Throttled = "Connection.Throttled";

        /// <summary>Connection timed out.</summary>
        public const string Timeout = "Connection.Timeout";

        /// <summary>The specified environment was not found.</summary>
        public const string EnvironmentNotFound = "Connection.EnvironmentNotFound";

        /// <summary>Multiple environments matched the query.</summary>
        public const string AmbiguousEnvironment = "Connection.AmbiguousEnvironment";

        /// <summary>Environment URL is invalid or malformed.</summary>
        public const string InvalidEnvironmentUrl = "Connection.InvalidEnvironmentUrl";

        /// <summary>Environment discovery failed.</summary>
        public const string DiscoveryFailed = "Connection.DiscoveryFailed";
    }

    /// <summary>
    /// Validation-related errors.
    /// </summary>
    public static class Validation
    {
        /// <summary>A required field is missing.</summary>
        public const string RequiredField = "Validation.RequiredField";

        /// <summary>A field has an invalid value.</summary>
        public const string InvalidValue = "Validation.InvalidValue";

        /// <summary>The specified file was not found.</summary>
        public const string FileNotFound = "Validation.FileNotFound";

        /// <summary>The specified directory was not found.</summary>
        public const string DirectoryNotFound = "Validation.DirectoryNotFound";

        /// <summary>Schema validation failed.</summary>
        public const string SchemaInvalid = "Validation.SchemaInvalid";

        /// <summary>Invalid command-line argument combination.</summary>
        public const string InvalidArguments = "Validation.InvalidArguments";

        /// <summary>URL scheme is not permitted (only http/https may be opened externally).</summary>
        public const string InvalidUrlScheme = "Validation.InvalidUrlScheme";

        /// <summary>
        /// A user-supplied path resolved outside the allowed workspace root.
        /// Raised by RPC handlers and CLI commands that reject arbitrary filesystem access.
        /// </summary>
        public const string PathOutsideWorkspace = "Validation.PathOutsideWorkspace";
    }

    /// <summary>
    /// Operation-related errors.
    /// </summary>
    public static class Operation
    {
        /// <summary>The requested resource was not found.</summary>
        public const string NotFound = "Operation.NotFound";

        /// <summary>A duplicate resource was detected.</summary>
        public const string Duplicate = "Operation.Duplicate";

        /// <summary>A dependency is missing or invalid.</summary>
        public const string Dependency = "Operation.Dependency";

        /// <summary>Some items in a batch operation failed.</summary>
        public const string PartialFailure = "Operation.PartialFailure";

        /// <summary>The operation was cancelled.</summary>
        public const string Cancelled = "Operation.Cancelled";

        /// <summary>The operation timed out.</summary>
        public const string Timeout = "Operation.Timeout";

        /// <summary>An internal error occurred.</summary>
        public const string Internal = "Operation.Internal";

        /// <summary>The operation is not supported.</summary>
        public const string NotSupported = "Operation.NotSupported";

        /// <summary>A conflicting operation is already in progress.</summary>
        public const string InProgress = "Operation.InProgress";
    }

    /// <summary>
    /// Query-related errors.
    /// </summary>
    public static class Query
    {
        /// <summary>SQL parse error.</summary>
        public const string ParseError = "Query.ParseError";

        /// <summary>Invalid FetchXML syntax.</summary>
        public const string InvalidFetchXml = "Query.InvalidFetchXml";

        /// <summary>Query execution failed.</summary>
        public const string ExecutionFailed = "Query.ExecutionFailed";

        /// <summary>Aggregate query exceeded the Dataverse 50,000-record limit.</summary>
        public const string AggregateLimitExceeded = "Query.AggregateLimitExceeded";

        /// <summary>DML statement blocked by safety guard (e.g., DELETE/UPDATE without WHERE).</summary>
        public const string DmlBlocked = "Query.DmlBlocked";

        /// <summary>DML operation would affect more rows than the configured cap.</summary>
        public const string DmlRowCapExceeded = "Query.DmlRowCapExceeded";

        /// <summary>Query plan generation timed out.</summary>
        public const string PlanTimeout = "Query.PlanTimeout";

        /// <summary>Expression type mismatch (e.g., comparing string to int).</summary>
        public const string TypeMismatch = "Query.TypeMismatch";

        /// <summary>Query exceeded the in-memory working set limit.</summary>
        public const string MemoryLimitExceeded = "Query.MemoryLimitExceeded";

        /// <summary>IntelliSense completion lookup failed.</summary>
        public const string CompletionFailed = "Query.CompletionFailed";

        /// <summary>SQL validation failed.</summary>
        public const string ValidationFailed = "Query.ValidationFailed";

        /// <summary>DML operation requires user confirmation before execution.</summary>
        public const string DmlConfirmationRequired = "Query.DmlConfirmationRequired";

        /// <summary>Query is incompatible with TDS Endpoint (DML, unsupported entity, unsupported feature).</summary>
        public const string TdsIncompatible = "Query.TdsIncompatible";

        /// <summary>TDS Endpoint connection failed (endpoint may be disabled on environment).</summary>
        public const string TdsConnectionFailed = "Query.TdsConnectionFailed";

        /// <summary>Subquery returned more than one row.</summary>
        public const string SubqueryMultipleRows = "Query.SubqueryMultipleRows";
    }

    /// <summary>
    /// External service errors.
    /// </summary>
    public static class External
    {
        /// <summary>GitHub API call failed.</summary>
        public const string GitHubApiError = "External.GitHubApiError";

        /// <summary>GitHub authentication failed.</summary>
        public const string GitHubAuthError = "External.GitHubAuthError";

        /// <summary>External service is unavailable.</summary>
        public const string ServiceUnavailable = "External.ServiceUnavailable";
    }

    /// <summary>
    /// Solution-related errors.
    /// </summary>
    public static class Solution
    {
        /// <summary>The requested solution was not found.</summary>
        public const string NotFound = "Solution.NotFound";

        /// <summary>Failed to list solutions from Dataverse.</summary>
        public const string ListFailed = "Solution.ListFailed";

        /// <summary>Failed to retrieve solution details from Dataverse.</summary>
        public const string GetFailed = "Solution.GetFailed";

        /// <summary>Failed to retrieve solution components from Dataverse.</summary>
        public const string GetComponentsFailed = "Solution.GetComponentsFailed";

        /// <summary>Failed to export the solution from Dataverse.</summary>
        public const string ExportFailed = "Solution.ExportFailed";

        /// <summary>Failed to import the solution into Dataverse.</summary>
        public const string ImportFailed = "Solution.ImportFailed";

        /// <summary>Failed to publish all customizations.</summary>
        public const string PublishFailed = "Solution.PublishFailed";
    }

    /// <summary>
    /// Plugin trace log errors.
    /// </summary>
    public static class PluginTrace
    {
        /// <summary>Failed to list plugin trace logs from Dataverse.</summary>
        public const string ListFailed = "PluginTrace.ListFailed";

        /// <summary>Failed to count plugin trace logs in Dataverse.</summary>
        public const string CountFailed = "PluginTrace.CountFailed";

        /// <summary>Failed to retrieve plugin trace log settings.</summary>
        public const string GetSettingsFailed = "PluginTrace.GetSettingsFailed";

        /// <summary>Failed to update plugin trace log settings.</summary>
        public const string SetSettingsFailed = "PluginTrace.SetSettingsFailed";
    }

    /// <summary>
    /// Plugin-related errors.
    /// </summary>
    public static class Plugin
    {
        /// <summary>Plugin entity not found.</summary>
        public const string NotFound = "Plugin.NotFound";

        /// <summary>Assembly or package has no binary content.</summary>
        public const string NoContent = "Plugin.NoContent";

        /// <summary>Message does not support plugin images.</summary>
        public const string ImageNotSupported = "Plugin.ImageNotSupported";

        /// <summary>Entity has child components that must be removed first.</summary>
        public const string HasChildren = "Plugin.HasChildren";

        /// <summary>Specified user for impersonation was not found.</summary>
        public const string UserNotFound = "Plugin.UserNotFound";

        /// <summary>Failed to enable or disable a plugin processing step.</summary>
        public const string SetStateFailed = "Plugin.SetStateFailed";
    }

    /// <summary>
    /// Update check and self-update errors.
    /// </summary>
    public static class UpdateCheck
    {
        /// <summary>NuGet API query failed (network, timeout, non-2xx).</summary>
        public const string NetworkError = "UpdateCheck.NetworkError";

        /// <summary>Cache file is corrupt or unreadable.</summary>
        public const string CacheCorrupt = "UpdateCheck.CacheCorrupt";

        /// <summary>Cannot locate the dotnet runtime executable.</summary>
        public const string DotnetNotFound = "UpdateCheck.DotnetNotFound";

        /// <summary>The dotnet tool update process exited with non-zero.</summary>
        public const string UpdateFailed = "UpdateCheck.UpdateFailed";
    }

    /// <summary>
    /// Service endpoint operation errors.
    /// </summary>
    public static class ServiceEndpoint
    {
        /// <summary>Service endpoint not found by ID or name.</summary>
        public const string NotFound = "ServiceEndpoint.NotFound";

        /// <summary>Service endpoint name is already in use.</summary>
        public const string NameInUse = "ServiceEndpoint.NameInUse";

        /// <summary>Input validation failed (URL, SAS key length, namespace format, etc.).</summary>
        public const string ValidationFailed = "ServiceEndpoint.ValidationFailed";

        /// <summary>Service endpoint has dependent step registrations that must be removed first.</summary>
        public const string HasDependents = "ServiceEndpoint.HasDependents";
    }

    /// <summary>
    /// Custom API operation errors.
    /// </summary>
    public static class CustomApi
    {
        /// <summary>Custom API not found by ID or unique name.</summary>
        public const string NotFound = "CustomApi.NotFound";

        /// <summary>Custom API unique name is already in use.</summary>
        public const string NameInUse = "CustomApi.NameInUse";

        /// <summary>Input validation failed (binding type, bound entity, parameter type, etc.).</summary>
        public const string ValidationFailed = "CustomApi.ValidationFailed";

        /// <summary>Custom API has parameters/properties that must be removed first (use --force to cascade).</summary>
        public const string HasDependents = "CustomApi.HasDependents";

        /// <summary>Parameter or response property not found by ID.</summary>
        public const string ParameterNotFound = "CustomApi.ParameterNotFound";

        /// <summary>Plugin type not found by name.</summary>
        public const string PluginTypeNotFound = "CustomApi.PluginTypeNotFound";
    }

    /// <summary>
    /// Data provider operation errors.
    /// </summary>
    public static class DataProvider
    {
        /// <summary>Data provider not found by ID or name.</summary>
        public const string NotFound = "DataProvider.NotFound";

        /// <summary>Input validation failed (name, data source, plugin type, etc.).</summary>
        public const string ValidationFailed = "DataProvider.ValidationFailed";
    }

    /// <summary>
    /// Data source operation errors.
    /// </summary>
    public static class DataSource
    {
        /// <summary>Data source not found by ID or name.</summary>
        public const string NotFound = "DataSource.NotFound";

        /// <summary>Data source has dependent data providers that must be removed first.</summary>
        public const string HasDependents = "DataSource.HasDependents";
    }

    /// <summary>
    /// Metadata authoring errors.
    /// </summary>
    public static class MetadataAuthoring
    {
        /// <summary>Metadata authoring validation failed (schema name, required fields, constraints).</summary>
        public const string ValidationFailed = "MetadataAuthoring.ValidationFailed";

        /// <summary>A general metadata authoring operation failed.</summary>
        public const string OperationFailed = "MetadataAuthoring.OperationFailed";
    }

    /// <summary>
    /// Web resource operation errors.
    /// </summary>
    public static class WebResource
    {
        /// <summary>Web resource not found by ID.</summary>
        public const string NotFound = "WebResource.NotFound";

        /// <summary>Attempted to edit a binary web resource (PNG/JPG/GIF/ICO/XAP).</summary>
        public const string NotEditable = "WebResource.NotEditable";

        /// <summary>Content conflict — server version has changed since last fetch.</summary>
        public const string Conflict = "WebResource.Conflict";

        /// <summary>Publish failed for one or more web resources.</summary>
        public const string PublishFailed = "WebResource.PublishFailed";

        /// <summary>Multiple web resources matched a partial name.</summary>
        public const string Ambiguous = "WebResource.Ambiguous";
    }

    /// <summary>
    /// Safety-guard errors (e.g., refused mutations during shakedown).
    /// </summary>
    public static class Safety
    {
        /// <summary>A mutation was refused because a shakedown session is active.
        /// Bypass: unset PPDS_SHAKEDOWN and/or remove .claude/state/shakedown-active.json.</summary>
        public const string ShakedownActive = "Safety.ShakedownActive";
    }
}

namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// Hierarchical error codes for structured error reporting.
/// Format: Category.Subcategory (e.g., "Auth.ProfileNotFound").
/// </summary>
/// <remarks>
/// These codes are designed for programmatic error handling by consumers
/// (VS Code extension, scripts, CI/CD pipelines). Use the hierarchical
/// format to enable both specific and category-level error matching.
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
    }

    /// <summary>
    /// Session orchestration errors.
    /// </summary>
    public static class Session
    {
        /// <summary>The requested session was not found.</summary>
        public const string NotFound = "Session.NotFound";

        /// <summary>A session for this issue already exists.</summary>
        public const string AlreadyExists = "Session.AlreadyExists";

        /// <summary>Failed to create worktree for session.</summary>
        public const string WorktreeCreationFailed = "Session.WorktreeCreationFailed";

        /// <summary>Failed to spawn worker terminal.</summary>
        public const string SpawnFailed = "Session.SpawnFailed";

        /// <summary>Invalid session status transition.</summary>
        public const string InvalidTransition = "Session.InvalidTransition";
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

        /// <summary>Cannot modify managed component.</summary>
        public const string ManagedComponent = "Plugin.ManagedComponent";

        /// <summary>Entity has child components that must be removed first.</summary>
        public const string HasChildren = "Plugin.HasChildren";
    }
}

using PPDS.Cli.Infrastructure.Progress;

namespace PPDS.Cli.Services;

/// <summary>
/// Service for managing Dataverse service endpoints (Azure Service Bus, EventHub) and webhooks.
/// </summary>
public interface IServiceEndpointService
{
    /// <summary>
    /// Lists all service endpoints in the environment.
    /// </summary>
    Task<List<ServiceEndpointInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a service endpoint by its ID.
    /// </summary>
    Task<ServiceEndpointInfo?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a service endpoint by name.
    /// </summary>
    Task<ServiceEndpointInfo?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an HTTP webhook endpoint.
    /// </summary>
    /// <param name="registration">Webhook registration parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new endpoint ID.</returns>
    Task<Guid> RegisterWebhookAsync(WebhookRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an Azure Service Bus endpoint (Queue, Topic, or EventHub).
    /// </summary>
    /// <param name="registration">Service Bus registration parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new endpoint ID.</returns>
    Task<Guid> RegisterServiceBusAsync(ServiceBusRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates properties of an existing service endpoint.
    /// </summary>
    Task UpdateAsync(Guid id, ServiceEndpointUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters a service endpoint.
    /// </summary>
    /// <param name="id">The endpoint ID.</param>
    /// <param name="force">If true, cascade delete dependent step registrations. If false, fails when dependents exist.</param>
    /// <param name="progressReporter">Optional progress reporter (used during cascade delete).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnregisterAsync(
        Guid id,
        bool force = false,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model for a Dataverse service endpoint.
/// </summary>
public record ServiceEndpointInfo
{
    /// <summary>The entity ID.</summary>
    public Guid Id { get; init; }

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Contract type: Queue, Topic, EventHub, Webhook, Rest, OneWay, TwoWay.
    /// </summary>
    public string ContractType { get; init; } = "";

    /// <summary>True when this is an HTTP webhook (contract = Webhook).</summary>
    public bool IsWebhook { get; init; }

    /// <summary>Webhook URL (webhooks only).</summary>
    public string? Url { get; init; }

    /// <summary>Service Bus namespace address (service bus endpoints only).</summary>
    public string? NamespaceAddress { get; init; }

    /// <summary>Queue/topic/eventhub name (service bus endpoints only).</summary>
    public string? Path { get; init; }

    /// <summary>Authentication type: SASKey, SASToken, HttpHeader, WebhookKey, HttpQueryString.</summary>
    public string AuthType { get; init; } = "";

    /// <summary>Message format: BinaryXML, Json, TextXML (service bus endpoints only).</summary>
    public string? MessageFormat { get; init; }

    /// <summary>User claim: None, UserId (service bus endpoints only).</summary>
    public string? UserClaim { get; init; }

    /// <summary>True when this endpoint is managed (part of a managed solution).</summary>
    public bool IsManaged { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>UTC last-modified timestamp.</summary>
    public DateTime? ModifiedOn { get; init; }
}

/// <summary>
/// Parameters for registering an HTTP webhook endpoint.
/// </summary>
/// <param name="Name">Unique display name for the endpoint.</param>
/// <param name="Url">Absolute HTTPS/HTTP URL of the webhook receiver.</param>
/// <param name="AuthType">Authentication type: WebhookKey, HttpHeader, or HttpQueryString.</param>
/// <param name="AuthValue">
/// Auth secret value. For WebhookKey: plain string.
/// For HttpHeader/HttpQueryString: XML formatted as
/// <c>&lt;settings&gt;&lt;setting name="X" value="Y" /&gt;&lt;/settings&gt;</c>.
/// </param>
public record WebhookRegistration(string Name, string Url, string AuthType, string? AuthValue = null);

/// <summary>
/// Parameters for registering an Azure Service Bus endpoint.
/// </summary>
/// <param name="Name">Unique display name for the endpoint.</param>
/// <param name="NamespaceAddress">Service Bus namespace address (must start with "sb://").</param>
/// <param name="Path">Queue, topic, or event hub name.</param>
/// <param name="Contract">Contract type: Queue, Topic, EventHub, OneWay, TwoWay, Rest.</param>
/// <param name="AuthType">Authentication type: SASKey or SASToken.</param>
/// <param name="SasKeyName">SAS key name (required when AuthType = SASKey).</param>
/// <param name="SasKey">SAS key value — exactly 44 characters (required when AuthType = SASKey).</param>
/// <param name="SasToken">SAS token value (required when AuthType = SASToken).</param>
/// <param name="MessageFormat">Optional message format: BinaryXML, Json, or TextXML.</param>
/// <param name="UserClaim">Optional user claim: None or UserId.</param>
public record ServiceBusRegistration(
    string Name,
    string NamespaceAddress,
    string Path,
    string Contract,
    string AuthType,
    string? SasKeyName = null,
    string? SasKey = null,
    string? SasToken = null,
    string? MessageFormat = null,
    string? UserClaim = null);

/// <summary>
/// Request for updating an existing service endpoint.
/// All fields are optional; only non-null values are applied.
/// </summary>
public record ServiceEndpointUpdateRequest(
    string? Name = null,
    string? Description = null,
    string? Url = null,
    string? AuthType = null,
    string? AuthValue = null);

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// The organisation's base language (<c>organization.languagecode</c>): the LCID every metadata label is authored in.
/// Dataverse requires a base-language label on every component; other provisioned languages are translations of it.
/// </summary>
public interface IOrganizationBaseLanguageProvider
{
    /// <summary>Returns the base-language LCID of the environment this provider is bound to.</summary>
    Task<int> GetBaseLanguageAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads <c>organization.languagecode</c> once per environment (one instance per connection pool) and caches the
/// value; a failed read is not cached, so the next call retries. The read uses its own pooled lease, released before
/// the value is returned. Failures are classified like the other metadata reads: authentication failures stay
/// <see cref="PpdsAuthException"/> with the re-authentication signal, connection failures keep their code, and anything
/// else (including a response without a usable language) is <see cref="MetadataErrorCodes.SdkOperationFailed"/>.
/// </summary>
public sealed class OrganizationBaseLanguageProvider : IOrganizationBaseLanguageProvider
{
    private const string Message = "Could not read the organization's base language (organization.languagecode), which labels are written in.";
    private readonly IDataverseConnectionPool _pool;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _cached;

    /// <summary>Binds the provider to one environment's connection pool.</summary>
    public OrganizationBaseLanguageProvider(IDataverseConnectionPool pool)
        => _pool = pool ?? throw new ArgumentNullException(nameof(pool));

    /// <inheritdoc />
    public async Task<int> GetBaseLanguageAsync(CancellationToken ct = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached > 0)
            return cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached > 0)
                return _cached;
            var lcid = await ReadAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _cached, lcid);
            return lcid;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ReadAsync(CancellationToken ct)
    {
        OrganizationResponse response;
        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
            response = await client.ExecuteAsync(new RetrieveMultipleRequest
            {
                Query = new QueryExpression("organization") { ColumnSet = new ColumnSet("languagecode"), TopCount = 1 }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not PpdsException and not MetadataValidationException)
        {
            var (error, exitCode) = ExceptionMapper.MapWithExitCode(ex);
            if (exitCode == ExitCodes.AuthError)
                throw new PpdsAuthException(error.Code, Message, ex)
                {
                    RequiresReauthentication = ex is DataverseAuthenticationException { RequiresReauthentication: true }
                };
            var code = exitCode == ExitCodes.ConnectionError ? error.Code : MetadataErrorCodes.SdkOperationFailed;
            throw new PpdsException(code, Message, ex);
        }

        var lcid = (response as RetrieveMultipleResponse)?.EntityCollection?.Entities?.FirstOrDefault()?.GetAttributeValue<int?>("languagecode");
        return lcid is > 0
            ? lcid.Value
            : throw new PpdsException(MetadataErrorCodes.SdkOperationFailed, Message + " Dataverse returned no language.");
    }
}

/// <summary>A fixed base language, for callers that already know it.</summary>
public sealed class FixedOrganizationBaseLanguage : IOrganizationBaseLanguageProvider
{
    private readonly int _lcid;

    /// <summary>Uses <paramref name="lcid"/> as the base language.</summary>
    public FixedOrganizationBaseLanguage(int lcid)
        => _lcid = lcid > 0 ? lcid : throw new ArgumentOutOfRangeException(nameof(lcid));

    /// <inheritdoc />
    public Task<int> GetBaseLanguageAsync(CancellationToken ct = default) => Task.FromResult(_lcid);
}

using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using Xunit.Sdk;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Temporarily switches the CALLING user's Dataverse UI language (<c>usersettings.uilanguageid</c>) for a live test and
/// puts it back. Only that one column of the calling user's own <c>usersettings</c> row is ever written; the user is
/// fixed by WhoAmI when the switch is prepared, and every later read or write re-checks WhoAmI on the connection it
/// uses, so a connection for another identity is refused rather than written through.
/// <para>
/// Guards: <see cref="PrepareAsync"/> records the original value and refuses to start when it is not the organisation's
/// base language, so a value left behind by a crashed run is reported instead of being adopted as the "original".
/// <see cref="RestoreAsync"/> writes the original value back (retrying a failed write), re-reads it and throws, naming
/// the user and both values, when it is not back. The caller runs it in a finally block. A killed test process cannot
/// restore; the scratch tool's <c>restore-ui-language 1033</c> is the manual safety net.
/// </para>
/// </summary>
public sealed class CallerUiLanguageSwitch
{
    private readonly Action<string> _log;
    private bool _writeAttempted;

    private CallerUiLanguageSwitch(Guid userId, int original, int target, int baseLanguage, Action<string> log)
    {
        UserId = userId;
        Original = original;
        Target = target;
        BaseLanguage = baseLanguage;
        _log = log;
    }

    /// <summary>The calling user whose setting is switched.</summary>
    public Guid UserId { get; }

    /// <summary>The value read before anything was written (always the base language).</summary>
    public int Original { get; }

    /// <summary>The UI language the test switches to.</summary>
    public int Target { get; }

    /// <summary>The organisation's base language.</summary>
    public int BaseLanguage { get; }

    /// <summary>Reads and checks the current value. Writes nothing.</summary>
    public static async Task<CallerUiLanguageSwitch> PrepareAsync(IDataverseConnectionPool pool, int target, Action<string> log)
    {
        await using var client = await pool.GetClientAsync();
        var userId = ((WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest())).UserId;
        var current = await ReadUiLanguageAsync(client, userId);
        var org = (await client.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet("languagecode") })).Entities.Single();
        var baseLanguage = org.GetAttributeValue<int>("languagecode");
        log($"OBSERVED UI language before the switch: user {userId} usersettings.uilanguageid {current}; organisation base language {baseLanguage}.");

        if (current != baseLanguage)
            throw new XunitException(
                $"Refusing to switch the UI language of user {userId}: usersettings.uilanguageid is {current}, not the organisation's base " +
                $"language {baseLanguage}. A previous run may have stopped before restoring it. Check it with the scratch tool's " +
                "'ui-language-status' and restore it by hand ('restore-ui-language 1033') before re-running.");
        if (target == baseLanguage)
            throw new XunitException($"The target UI language {target} is the base language; nothing would be switched.");
        return new CallerUiLanguageSwitch(userId, current, target, baseLanguage, log);
    }

    /// <summary>Sets the calling user's UI language to <see cref="Target"/> and confirms it by re-reading.</summary>
    public async Task ApplyAsync(IDataverseConnectionPool pool)
    {
        await using (var client = await pool.GetClientAsync())
        {
            var current = await ReadUiLanguageAsync(client, UserId);
            if (current != Original)
                throw new XunitException(
                    $"Refusing to switch: user {UserId} usersettings.uilanguageid changed from {Original} to {current} since it was read.");
            // Marked before the write: a write that fails ambiguously (e.g. a timeout after the server committed)
            // must still be restored.
            _writeAttempted = true;
            await WriteUiLanguageAsync(client, UserId, Target);
        }

        await using var reread = await pool.GetClientAsync();
        var now = await ReadUiLanguageAsync(reread, UserId);
        _log($"OBSERVED UI language during the test: user {UserId} usersettings.uilanguageid {now} (switched from {Original}).");
        if (now != Target)
            throw new XunitException($"The switch did not take: user {UserId} usersettings.uilanguageid reads {now}, expected {Target}.");
    }

    /// <summary>
    /// Writes <see cref="Original"/> back when the current value differs, re-reads it, and throws when it is not back.
    /// Does nothing (but still reports the value) when no write was attempted.
    /// </summary>
    public async Task RestoreAsync(IDataverseConnectionPool pool)
    {
        if (_writeAttempted)
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await using var client = await pool.GetClientAsync();
                    if (await ReadUiLanguageAsync(client, UserId) != Original)
                        await WriteUiLanguageAsync(client, UserId, Original);
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _log($"Restore attempt {attempt} for user {UserId} failed: {ex.GetType().Name}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            if (last is not null)
                _log($"All restore attempts failed; re-reading to report the state. Last error: {last.Message}");
        }

        int after;
        try
        {
            await using var client = await pool.GetClientAsync();
            after = await ReadUiLanguageAsync(client, UserId);
        }
        catch (Exception ex)
        {
            throw new XunitException(
                $"UI LANGUAGE NOT CONFIRMED RESTORED: could not re-read usersettings.uilanguageid of user {UserId} " +
                $"(original {Original}, switched to {Target}): {ex.GetType().Name}: {ex.Message}. Check with 'ui-language-status'.");
        }

        _log($"OBSERVED UI language after the restore: user {UserId} usersettings.uilanguageid {after} (original {Original}).");
        if (after != Original)
            throw new XunitException(
                $"UI LANGUAGE NOT RESTORED: user {UserId} usersettings.uilanguageid is {after}, expected the original {Original} " +
                $"(the test switched it to {Target}). Restore it with the scratch tool: 'restore-ui-language {Original}'.");
    }

    private static async Task<int> ReadUiLanguageAsync(IPooledClient client, Guid userId)
    {
        await EnsureCallerAsync(client, userId);
        var query = new QueryExpression("usersettings") { ColumnSet = new ColumnSet("systemuserid", "uilanguageid") };
        query.Criteria.AddCondition("systemuserid", ConditionOperator.Equal, userId);
        var rows = (await client.RetrieveMultipleAsync(query)).Entities;
        if (rows.Count != 1 || rows[0].GetAttributeValue<Guid>("systemuserid") != userId)
            throw new XunitException($"Expected exactly one usersettings row for user {userId}; found {rows.Count}.");
        return rows[0].GetAttributeValue<int>("uilanguageid");
    }

    /// <summary>Writes only <c>uilanguageid</c> on the calling user's own row.</summary>
    private static async Task WriteUiLanguageAsync(IPooledClient client, Guid userId, int value)
    {
        await EnsureCallerAsync(client, userId);
        var row = new Entity("usersettings", userId) { ["uilanguageid"] = value };
        await client.ExecuteAsync(new UpdateRequest { Target = row });
    }

    private static async Task EnsureCallerAsync(IPooledClient client, Guid userId)
    {
        var caller = ((WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest())).UserId;
        if (caller != userId)
            throw new XunitException($"Refusing: this connection is user {caller}, not the user {userId} whose UI language is being switched.");
    }
}

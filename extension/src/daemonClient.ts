import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { spawn, ChildProcess } from 'child_process';
import {
    createMessageConnection,
    MessageConnection,
    StreamMessageReader,
    StreamMessageWriter,
    CancellationToken
} from 'vscode-jsonrpc/node';
import type {
    AuthListResponse,
    AuthWhoResponse,
    AuthSelectResponse,
    EnvListResponse,
    EnvSelectResponse,
    EnvConfigGetResponse,
    EnvConfigSetResponse,
    EnvWhoResponse,
    QueryResultResponse,
    QueryCompleteResponse,
    QueryHistoryListResponse,
    QueryHistoryDeleteResponse,
    QueryExportResponse,
    QueryExplainResponse,
    ProfileCreateResponse,
    ProfileDeleteResponse,
    ProfileRenameResponse,
    ProfilesInvalidateResponse,
    SolutionsListResponse,
    SolutionComponentsResponse,
    SchemaEntitiesResponse,
    SchemaAttributesResponse,
} from './types.js';

// Re-export types that other modules may need via daemonClient
export type {
    AuthListResponse,
    AuthWhoResponse,
    AuthSelectResponse,
    EnvListResponse,
    EnvSelectResponse,
    EnvConfigGetResponse,
    EnvConfigSetResponse,
    EnvWhoResponse,
    QueryResultResponse,
    QueryCompleteResponse,
    QueryHistoryListResponse,
    QueryHistoryDeleteResponse,
    QueryExportResponse,
    QueryExplainResponse,
    ProfileCreateResponse,
    ProfileDeleteResponse,
    ProfileRenameResponse,
    ProfilesInvalidateResponse,
    SolutionsListResponse,
    SolutionComponentsResponse,
    SchemaEntitiesResponse,
    SchemaAttributesResponse,
} from './types.js';

/**
 * Client for communicating with the ppds serve daemon via JSON-RPC.
 *
 * All RPC methods call ensureConnected() first, which starts the daemon
 * process if it is not already running. If the daemon dies, the next RPC
 * call will automatically restart it (auto-reconnect).
 */
export class DaemonClient implements vscode.Disposable {
    private static readonly STARTUP_TIMEOUT_MS = 30_000;

    private process: ChildProcess | null = null;
    private connection: MessageConnection | null = null;
    private connectingPromise: Promise<void> | null = null;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    private pendingNotificationHandlers: Array<{ method: string; handler: (...args: any[]) => void }> = [];
    private _disposed = false;
    private extensionPath: string;

    constructor(extensionPath: string, private readonly log: vscode.LogOutputChannel) {
        this.extensionPath = extensionPath;
    }

    /**
     * Resolves the path to the ppds CLI binary.
     * Resolution order:
     *   1. Bundled binary in extension/bin/ (marketplace install)
     *   2. Debug build output from src/PPDS.Cli/ (F5 development)
     *   3. PATH fallback (global dotnet tool)
     */
    private resolvePpdsPath(): string {
        const ext = process.platform === 'win32' ? '.exe' : '';

        // 1. Bundled binary (marketplace / bundle-cli.js output)
        const bundledPath = path.join(this.extensionPath, 'bin', `ppds${ext}`);
        try {
            fs.accessSync(bundledPath, fs.constants.X_OK);
            return bundledPath;
        } catch { /* not bundled */ }

        // 2. Debug build output (F5 development — extensionPath is extension/)
        const debugPath = path.join(
            this.extensionPath, '..', 'src', 'PPDS.Cli', 'bin', 'Debug', 'net8.0', `ppds${ext}`
        );
        try {
            fs.accessSync(debugPath, fs.constants.X_OK);
            this.log.info(`Using debug build: ${debugPath}`);
            return debugPath;
        } catch { /* no debug build */ }

        // 3. PATH fallback
        return 'ppds';
    }

    /**
     * Starts the daemon process and establishes JSON-RPC connection
     */
    async start(): Promise<void> {
        if (this.connection) {
            return; // Already running
        }

        const ppdsPath = this.resolvePpdsPath();
        this.log.info(`Starting ppds serve daemon (${ppdsPath})...`);

        // Spawn the daemon process
        this.process = spawn(ppdsPath, ['serve'], {
            stdio: ['pipe', 'pipe', 'pipe']
        });

        if (!this.process.stdout || !this.process.stdin) {
            throw new Error('Failed to create daemon process streams');
        }

        // Log stderr for debugging
        this.process.stderr?.on('data', (data: Buffer) => {
            this.log.warn(`[daemon stderr] ${data.toString()}`);
        });

        this.process.on('error', (err) => {
            this.log.error(`Daemon error: ${err.message}`);
            vscode.window.showErrorMessage(`PPDS daemon error: ${err.message}`);
        });

        // Startup exit detection: rejects if the process exits before the
        // handshake completes. Removed after handshake to avoid unhandled rejections.
        let startupExitReject: ((err: Error) => void) | null = null;
        const exitPromise = new Promise<never>((_, reject) => {
            startupExitReject = reject;
        });
        // Prevent unhandled rejection if daemon exits in the narrow window
        // between handshake success and startupExitReject being nulled.
        exitPromise.catch(() => {});

        const onStartupExit = (code: number | null) => {
            this.log.error(`Daemon exited during startup with code ${code}`);
            this.connection = null;
            this.process = null;
            this.connectingPromise = null;
            if (startupExitReject) {
                startupExitReject(new Error(`Daemon exited during startup with code ${code}`));
            }
        };
        this.process.on('exit', onStartupExit);

        // Create JSON-RPC connection over stdio
        const reader = new StreamMessageReader(this.process.stdout);
        const writer = new StreamMessageWriter(this.process.stdin);
        const connection = createMessageConnection(reader, writer);

        // Guard: if dispose() was called while we were setting up, clean up and abort
        if (this._disposed) {
            connection.dispose();
            this.process?.kill();
            this.process = null;
            throw new Error('DaemonClient is disposed');
        }

        this.connection = connection;

        // Start listening for messages
        this.connection.listen();

        // Flush any notification handlers that were registered before connect
        for (const pending of this.pendingNotificationHandlers) {
            this.connection.onNotification(pending.method, pending.handler);
        }
        this.pendingNotificationHandlers = [];

        // Startup handshake: race a health check against the process exit event
        // and a startup timeout to detect immediate failures or hangs.
        let timeoutId: ReturnType<typeof setTimeout> | undefined;
        const timeoutPromise = new Promise<never>((_, reject) => {
            timeoutId = setTimeout(
                () => reject(new Error(`Daemon startup timed out after ${DaemonClient.STARTUP_TIMEOUT_MS / 1000}s`)),
                DaemonClient.STARTUP_TIMEOUT_MS
            );
        });

        try {
            await Promise.race([
                this.connection.sendRequest('auth/list'),
                exitPromise,
                timeoutPromise,
            ]);
        } catch (err) {
            this.connection?.dispose();
            this.connection = null;
            this.process?.kill();
            this.process = null;
            throw err;
        } finally {
            clearTimeout(timeoutId);
        }

        // Handshake succeeded — remove the startup exit listener to prevent
        // unhandled rejections, then install a post-startup exit listener for
        // auto-reconnect cleanup.
        startupExitReject = null;
        this.process.removeListener('exit', onStartupExit);

        this.process.on('exit', (code: number | null) => {
            this.log.warn(`Daemon exited with code ${code}`);
            this.connection = null;
            this.process = null;
        });

        this.log.info('Daemon connection established');
    }

    // ── Auth methods ────────────────────────────────────────────────────────

    /**
     * Lists all authentication profiles.
     */
    async authList(): Promise<AuthListResponse> {
        await this.ensureConnected();

        this.log.debug('Calling auth/list...');
        const result = await this.connection!.sendRequest<AuthListResponse>('auth/list');
        this.log.debug(`Got ${result.profiles.length} profiles`);

        return result;
    }

    /**
     * Gets detailed information about the currently active profile.
     */
    async authWho(): Promise<AuthWhoResponse> {
        await this.ensureConnected();

        this.log.debug('Calling auth/who...');
        const result = await this.connection!.sendRequest<AuthWhoResponse>('auth/who');
        this.log.debug(`auth/who returned profile index ${result.index}`);

        return result;
    }

    /**
     * Selects (activates) an authentication profile by index or name.
     */
    async authSelect(params: { index?: number; name?: string }): Promise<AuthSelectResponse> {
        await this.ensureConnected();

        this.log.info(`Calling auth/select with ${params.name ? `name="${params.name}"` : `index=${params.index}`}...`);
        const result = await this.connection!.sendRequest<AuthSelectResponse>('auth/select', params);
        this.log.debug(`Selected profile: ${result.name ?? result.identity}`);

        return result;
    }

    // ── Environment methods ─────────────────────────────────────────────────

    /**
     * Lists available Dataverse environments, optionally filtered.
     */
    async envList(filter?: string): Promise<EnvListResponse> {
        await this.ensureConnected();

        const params = filter !== undefined ? { filter } : {};
        this.log.debug(`Calling env/list${filter ? ` with filter="${filter}"` : ''}...`);
        const result = await this.connection!.sendRequest<EnvListResponse>('env/list', params);
        this.log.debug(`Got ${result.environments.length} environments`);

        return result;
    }

    /**
     * Selects (activates) a Dataverse environment by URL or name.
     */
    async envSelect(environment: string): Promise<EnvSelectResponse> {
        await this.ensureConnected();

        this.log.info(`Calling env/select for "${environment}"...`);
        const result = await this.connection!.sendRequest<EnvSelectResponse>('env/select', { environment });
        this.log.debug(`Selected environment: ${result.displayName} (${result.url})`);

        return result;
    }

    /**
     * Returns WhoAmI details for the active environment connection.
     * This is separate from authWho - it queries the live Dataverse connection.
     */
    async envWho(): Promise<EnvWhoResponse> {
        await this.ensureConnected();

        this.log.debug('Calling env/who...');
        const result = await this.connection!.sendRequest<EnvWhoResponse>('env/who');
        this.log.debug(`env/who: ${result.connectedAs} @ ${result.organizationName}`);

        return result;
    }

    /**
     * Gets the configuration for a specific environment.
     */
    async envConfigGet(environmentUrl: string): Promise<EnvConfigGetResponse> {
        await this.ensureConnected();

        this.log.debug(`Calling env/config/get for "${environmentUrl}"...`);
        const result = await this.connection!.sendRequest<EnvConfigGetResponse>('env/config/get', { environmentUrl });
        this.log.debug(`Got config: label=${result.label ?? '(none)'}, type=${result.type ?? '(none)'}, color=${result.color ?? '(none)'}`);

        return result;
    }

    /**
     * Sets the configuration for a specific environment.
     */
    async envConfigSet(params: {
        environmentUrl: string;
        label?: string;
        type?: string;
        color?: string;
    }): Promise<EnvConfigSetResponse> {
        await this.ensureConnected();

        this.log.debug(`Calling env/config/set for "${params.environmentUrl}"...`);
        const result = await this.connection!.sendRequest<EnvConfigSetResponse>('env/config/set', params);
        this.log.debug(`Config saved: saved=${result.saved}`);

        return result;
    }

    // ── Query methods ───────────────────────────────────────────────────────

    /**
     * Executes a SQL query against the active Dataverse environment.
     */
    async querySql(params: {
        sql: string;
        environmentUrl?: string;
        top?: number;
        page?: number;
        pagingCookie?: string;
        count?: boolean;
        showFetchXml?: boolean;
        useTds?: boolean;
    }, token?: CancellationToken): Promise<QueryResultResponse> {
        await this.ensureConnected();

        this.log.info(`Calling query/sql: ${params.sql.substring(0, 100)}...`);
        const result = token
            ? await this.connection!.sendRequest<QueryResultResponse>('query/sql', params, token)
            : await this.connection!.sendRequest<QueryResultResponse>('query/sql', params);
        this.log.debug(`Query returned ${result.count} records in ${result.executionTimeMs}ms`);

        return result;
    }

    /**
     * Executes a FetchXML query against the active Dataverse environment.
     */
    async queryFetch(params: {
        fetchXml: string;
        environmentUrl?: string;
        top?: number;
        page?: number;
        pagingCookie?: string;
        count?: boolean;
    }, token?: CancellationToken): Promise<QueryResultResponse> {
        await this.ensureConnected();

        this.log.info('Calling query/fetch...');
        const result = token
            ? await this.connection!.sendRequest<QueryResultResponse>('query/fetch', params, token)
            : await this.connection!.sendRequest<QueryResultResponse>('query/fetch', params);
        this.log.debug(`Query returned ${result.count} records in ${result.executionTimeMs}ms`);

        return result;
    }

    /**
     * Gets IntelliSense completion items for SQL or FetchXML.
     * Pass language='fetchxml' for FetchXML documents; omit or pass 'sql' for SQL.
     * Uses quiet (non-logging) transport to avoid flooding the output channel
     * on every keystroke.
     */
    async queryComplete(params: { sql: string; cursorOffset: number; language?: string }): Promise<QueryCompleteResponse> {
        return this.sendRequestQuiet<QueryCompleteResponse>('query/complete', params);
    }

    // ── Query History ────────────────────────────────────────────────────────

    /**
     * Lists query history entries.
     */
    async queryHistoryList(search?: string, limit?: number): Promise<QueryHistoryListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (search !== undefined) params.search = search;
        if (limit !== undefined) params.limit = limit;
        this.log.debug(`Calling query/history/list...`);
        const result = await this.connection!.sendRequest<QueryHistoryListResponse>('query/history/list', params);
        this.log.debug(`Got ${result.entries.length} history entries`);
        return result;
    }

    /**
     * Deletes a query history entry.
     */
    async queryHistoryDelete(id: string): Promise<QueryHistoryDeleteResponse> {
        await this.ensureConnected();
        this.log.debug(`Calling query/history/delete for "${id}"...`);
        const result = await this.connection!.sendRequest<QueryHistoryDeleteResponse>('query/history/delete', { id });
        this.log.debug(`Deleted: ${result.deleted}`);
        return result;
    }

    // ── Query Export & Explain ──────────────────────────────────────────────

    /**
     * Exports query results in the specified format (CSV, TSV, or JSON).
     * The daemon executes the query and formats results server-side.
     */
    async queryExport(params: {
        sql: string;
        environmentUrl?: string;
        format?: string;
        includeHeaders?: boolean;
        top?: number;
    }): Promise<QueryExportResponse> {
        await this.ensureConnected();
        this.log.info(`Calling query/export...`);
        const result = await this.connection!.sendRequest<QueryExportResponse>('query/export', params);
        this.log.debug(`Exported ${result.rowCount} rows as ${result.format}`);
        return result;
    }

    /**
     * Returns the execution plan (FetchXML) for a SQL query.
     * Since Dataverse SQL is transpiled to FetchXML, the transpiled FetchXML
     * serves as the execution plan.
     */
    async queryExplain(params: { sql: string; environmentUrl?: string }): Promise<QueryExplainResponse> {
        await this.ensureConnected();
        this.log.debug('Calling query/explain...');
        const result = await this.connection!.sendRequest<QueryExplainResponse>('query/explain', params);
        this.log.debug(`Got explain plan (${result.format})`);
        return result;
    }

    // ── Profile management ──────────────────────────────────────────────────

    /**
     * Creates a new authentication profile.
     */
    async profilesCreate(params: {
        name?: string;
        authMethod: string;
        environmentUrl?: string;
        applicationId?: string;
        clientSecret?: string;
        tenantId?: string;
        certificatePath?: string;
        certificatePassword?: string;
        certificateThumbprint?: string;
        username?: string;
        password?: string;
    }): Promise<ProfileCreateResponse> {
        await this.ensureConnected();

        this.log.info(`Calling profiles/create (method=${params.authMethod})...`);
        const result = await this.connection!.sendRequest<ProfileCreateResponse>('profiles/create', params);
        this.log.debug(`Profile created: index=${result.index}, name=${result.name ?? '(auto)'}`);

        return result;
    }

    /**
     * Deletes an authentication profile by index or name.
     */
    async profilesDelete(params: { index?: number; name?: string }): Promise<ProfileDeleteResponse> {
        await this.ensureConnected();

        this.log.info(`Calling profiles/delete with ${params.name ? `name="${params.name}"` : `index=${params.index}`}...`);
        const result = await this.connection!.sendRequest<ProfileDeleteResponse>('profiles/delete', params);
        this.log.debug(`Profile deleted: ${result.deleted}`);

        return result;
    }

    /**
     * Renames an authentication profile.
     */
    async profilesRename(currentName: string, newName: string): Promise<ProfileRenameResponse> {
        await this.ensureConnected();

        this.log.info(`Calling profiles/rename: "${currentName}" -> "${newName}"...`);
        const result = await this.connection!.sendRequest<ProfileRenameResponse>(
            'profiles/rename',
            { currentName, newName }
        );
        this.log.debug(`Profile renamed: ${result.previousName} -> ${result.newName}`);

        return result;
    }

    /**
     * Invalidates (clears cached tokens for) a profile.
     */
    async profilesInvalidate(profileName: string): Promise<ProfilesInvalidateResponse> {
        await this.ensureConnected();

        this.log.info(`Calling profiles/invalidate for "${profileName}"...`);
        const result = await this.connection!.sendRequest<ProfilesInvalidateResponse>(
            'profiles/invalidate',
            { profileName }
        );
        this.log.debug(`Profile "${profileName}" invalidated: ${result.invalidated}`);

        return result;
    }

    // ── Solutions ───────────────────────────────────────────────────────────

    /**
     * Lists solutions in the active Dataverse environment.
     */
    async solutionsList(filter?: string, includeManaged?: boolean, environmentUrl?: string): Promise<SolutionsListResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (filter !== undefined) {
            params.filter = filter;
        }
        if (includeManaged !== undefined) {
            params.includeManaged = includeManaged;
        }
        if (environmentUrl !== undefined) {
            params.environmentUrl = environmentUrl;
        }

        this.log.info(`Calling solutions/list${filter ? ` with filter="${filter}"` : ''}...`);
        const result = await this.connection!.sendRequest<SolutionsListResponse>('solutions/list', params);
        this.log.debug(`Got ${result.solutions.length} solutions`);

        return result;
    }

    /**
     * Lists components for a specific solution.
     */
    async solutionsComponents(uniqueName: string, componentType?: number, environmentUrl?: string): Promise<SolutionComponentsResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { uniqueName };
        if (componentType !== undefined) {
            params.componentType = componentType;
        }
        if (environmentUrl !== undefined) {
            params.environmentUrl = environmentUrl;
        }

        this.log.debug(`Calling solutions/components for "${uniqueName}"...`);
        const result = await this.connection!.sendRequest<SolutionComponentsResponse>('solutions/components', params);
        this.log.debug(`Got ${result.components.length} components`);

        return result;
    }

    // ── Schema ──────────────────────────────────────────────────────────────

    /**
     * Lists all entities in the active Dataverse environment.
     * Used for IntelliSense entity completion.
     */
    async schemaEntities(): Promise<SchemaEntitiesResponse> {
        await this.ensureConnected();

        this.log.debug('Calling schema/entities...');
        const result = await this.connection!.sendRequest<SchemaEntitiesResponse>('schema/entities');
        this.log.debug(`Got ${result.entities.length} entities`);

        return result;
    }

    /**
     * Lists attributes for a specific entity in the active Dataverse environment.
     * Used for IntelliSense attribute completion.
     */
    async schemaAttributes(entity: string): Promise<SchemaAttributesResponse> {
        await this.ensureConnected();

        this.log.debug(`Calling schema/attributes for "${entity}"...`);
        const result = await this.connection!.sendRequest<SchemaAttributesResponse>(
            'schema/attributes',
            { entity }
        );
        this.log.debug(`Got ${result.attributes.length} attributes for ${result.entityName}`);

        return result;
    }

    // ── Notifications ───────────────────────────────────────────────────────

    /**
     * Registers a handler for device code authentication notifications.
     * The daemon sends these when interactive browser-based auth is required.
     */
    onDeviceCode(handler: (params: { userCode: string; verificationUrl: string; message: string }) => void): void {
        const method = 'auth/deviceCode';
        if (this.connection) {
            this.connection.onNotification(method, handler);
        } else {
            this.pendingNotificationHandlers.push({ method, handler });
        }
        this.log.debug('Registered auth/deviceCode notification handler');
    }

    // ── Connection management ───────────────────────────────────────────────

    /**
     * Sends an RPC request without logging, for high-frequency calls such as
     * IntelliSense completions that would otherwise flood the output channel.
     */
    private async sendRequestQuiet<T>(method: string, params?: unknown): Promise<T> {
        await this.ensureConnected();
        return this.connection!.sendRequest<T>(method, params);
    }

    /**
     * Ensures the daemon is connected, starting it if necessary.
     * This provides auto-reconnect: if the daemon process dies, the next
     * RPC call will restart it since the exit handler sets connection to null.
     */
    private async ensureConnected(): Promise<void> {
        if (this._disposed) throw new Error('DaemonClient is disposed');
        if (this.connection) return;
        if (!this.connectingPromise) {
            this.connectingPromise = this.start().finally(() => {
                this.connectingPromise = null;
            });
        }
        await this.connectingPromise;
    }

    /**
     * Stops the daemon and cleans up resources
     */
    dispose(): void {
        this.log.info('Disposing daemon client...');

        this._disposed = true;
        this.connectingPromise = null;

        if (this.connection) {
            this.connection.dispose();
            this.connection = null;
        }

        if (this.process) {
            this.process.kill();
            this.process = null;
        }
    }
}

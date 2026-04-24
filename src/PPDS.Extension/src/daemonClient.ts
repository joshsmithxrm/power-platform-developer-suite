import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs';
import { spawn, ChildProcess } from 'child_process';

import * as vscode from 'vscode';
import {
    createMessageConnection,
    MessageConnection,
    StreamMessageReader,
    StreamMessageWriter,
    CancellationToken,
    RequestType,
    ParameterStructures,
} from 'vscode-jsonrpc/node';

import type {
    AuthListResponse,
    AuthWhoResponse,
    AuthSelectResponse,
    EnvListResponse,
    EnvSelectResponse,
    EnvConfigGetResponse,
    EnvConfigSetResponse,
    EnvConfigRemoveResponse,
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
    MetadataEntitiesResponse,
    MetadataEntityResponse,
    MetadataGlobalOptionSetsResponse,
    MetadataGlobalOptionSetDetailResponse,
    ImportJobsListResponse,
    ImportJobsGetResponse,
    PluginTracesListResponse,
    PluginTracesGetResponse,
    PluginTracesTimelineResponse,
    PluginTracesDeleteResponse,
    PluginTracesTraceLevelResponse,
    PluginTracesSetTraceLevelResponse,
    TraceFilterDto,
    ConnectionReferencesListResponse,
    ConnectionReferencesGetResponse,
    ConnectionReferencesAnalyzeResponse,
    EnvironmentVariablesListResponse,
    EnvironmentVariablesGetResponse,
    EnvironmentVariablesSetResponse,
    EnvironmentVariablesSyncDeploymentSettingsResponse,
    WebResourceInfoDto,
    WebResourceDetailDto,
    MetadataAuthoringResult,
    MetadataDeleteResult,
} from './types.js';

// Re-export AuthWhoResponse for profileCommands.ts convenience
export type { AuthWhoResponse } from './types.js';

// ── Plugins panel response types ─────────────────────────────────────────────
// NOTE: Only types consumed outside this file are exported. The rest are internal
// DTOs used only as return types for DaemonClient methods. Per knip audit (v1.0),
// keeping them non-exported prevents drift between the RPC handler (C#) and
// never-imported copies in downstream code. See G3 DTO drift check.

interface PluginsListResponse {
    assemblies: PluginAssemblyInfoDto[];
    packages: PluginPackageInfoDto[];
    serviceEndpoints: ServiceEndpointDto[];
    customApis: CustomApiDto[];
    dataSources: DataSourceDto[];
}
interface PluginPackageInfoDto { id?: string; name: string; uniqueName?: string; version?: string; assemblies: PluginAssemblyInfoDto[] }
export interface PluginAssemblyInfoDto { id?: string; name: string; version?: string; publicKeyToken?: string; types: PluginTypeInfoDto[] }
interface PluginTypeInfoDto { id?: string; typeName: string; steps: PluginStepInfoDto[] }
interface PluginStepInfoDto { id?: string; name: string; message: string; entity: string; stage: string; mode: string; executionOrder: number; filteringAttributes?: string; isEnabled: boolean; description?: string }
interface PluginsGetResponse {
    type: string;
    assembly?: Record<string, unknown>;
    package?: Record<string, unknown>;
    pluginType?: Record<string, unknown>;
    step?: Record<string, unknown>;
    image?: Record<string, unknown>;
}
interface PluginsMessagesResponse { messages: string[] }
interface PluginsEntityAttributesResponse { attributes: AttributeInfoDto[] }
interface AttributeInfoDto { logicalName: string; displayName: string; attributeType: string }
interface PluginsToggleStepResponse { success: boolean }
interface PluginsRegisterResponse { id: string }
interface PluginsUpdateResponse { success: boolean }
interface PluginsUnregisterResponse { deletedCount: number }
interface PluginsDownloadResponse { content: string; fileName: string }

// ── Service endpoints response types ─────────────────────────────────────────

interface ServiceEndpointsListResponse { endpoints: ServiceEndpointDto[] }
interface ServiceEndpointDto {
    id: string;
    name: string;
    description?: string;
    contractType: string;
    isWebhook: boolean;
    url?: string;
    namespaceAddress?: string;
    path?: string;
    authType: string;
    messageFormat?: string;
    userClaim?: string;
    isManaged: boolean;
}
interface ServiceEndpointsGetResponse { endpoint: ServiceEndpointDto }
interface ServiceEndpointsRegisterResponse { id: string }

// ── Custom API response types ─────────────────────────────────────────────────

interface CustomApisListResponse { apis: CustomApiDto[] }
interface CustomApiDto {
    id: string;
    uniqueName: string;
    displayName: string;
    name?: string;
    description?: string;
    pluginTypeId?: string;
    pluginTypeName?: string;
    bindingType: string;
    boundEntity?: string;
    allowedProcessingStepType: string;
    isFunction: boolean;
    isPrivate: boolean;
    executePrivilegeName?: string;
    isManaged: boolean;
    requestParameters: CustomApiParameterDto[];
    responseProperties: CustomApiParameterDto[];
}
interface CustomApiParameterDto {
    id: string;
    uniqueName: string;
    displayName: string;
    name?: string;
    description?: string;
    type: string;
    logicalEntityName?: string;
    isOptional: boolean;
    isManaged: boolean;
}
interface CustomApisGetResponse { api: CustomApiDto }
interface CustomApisRegisterResponse { id: string }

// ── Data provider response types ──────────────────────────────────────────────

interface DataProvidersListResponse { providers: DataProviderDto[] }
interface DataProviderDto {
    id: string;
    name: string;
    dataSourceId?: string;
    dataSourceName?: string;
    retrievePlugin?: string;
    retrieveMultiplePlugin?: string;
    createPlugin?: string;
    updatePlugin?: string;
    deletePlugin?: string;
    isManaged: boolean;
}
interface DataSourcesListResponse { dataSources: DataSourceDto[] }
interface DataSourceDto {
    id: string;
    name: string;
}

export type DaemonState = 'stopped' | 'starting' | 'ready' | 'error' | 'reconnecting';

export class RpcTimeoutError extends Error {
    constructor(label: string, timeoutMs: number) {
        super(`${label} timed out after ${timeoutMs}ms`);
        this.name = 'RpcTimeoutError';
    }
}

export function withRpcTimeout<T>(promise: Promise<T>, timeoutMs: number, label: string): Promise<T> {
    let timer: ReturnType<typeof setTimeout>;
    const timeoutPromise = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new RpcTimeoutError(label, timeoutMs)), timeoutMs);
    });
    return Promise.race([promise, timeoutPromise]).finally(() => clearTimeout(timer!));
}

/**
 * Client for communicating with the ppds serve daemon via JSON-RPC.
 *
 * All RPC methods call ensureConnected() first, which starts the daemon
 * process if it is not already running. If the daemon dies, the next RPC
 * call will automatically restart it (auto-reconnect).
 */
/** Maps daemon stderr log-level tags to LogOutputChannel method names. */
const DAEMON_LOG_LEVELS: Record<string, 'trace' | 'debug' | 'info' | 'warn' | 'error'> = {
    TRC: 'trace',
    DBG: 'debug',
    INF: 'info',
    WRN: 'warn',
    ERR: 'error',
    CRT: 'error',
};

const DAEMON_LOG_LEVEL_RE = /\]\s+\[(TRC|DBG|INF|WRN|ERR|CRT)\]/;

/** Parses the log level from a daemon stderr line. Returns 'warn' for unrecognized formats. */
export function parseDaemonLogLevel(line: string): 'trace' | 'debug' | 'info' | 'warn' | 'error' {
    const match = DAEMON_LOG_LEVEL_RE.exec(line);
    return match ? DAEMON_LOG_LEVELS[match[1]] : 'warn';
}

// RequestTypes with positional parameter encoding for DTO-based RPC methods.
// StreamJsonRpc binds positional params (JSON array) to a single DTO parameter,
// but vscode-jsonrpc defaults to named params (JSON object) which StreamJsonRpc
// interprets as individual method arguments. These force positional encoding.
// RequestType<Params, Result, Error> with positional encoding for DTO-based methods
const RPC_QUERY_SQL = new RequestType<Record<string, unknown>, QueryResultResponse, void>('query/sql', ParameterStructures.byPosition);
const RPC_QUERY_FETCH = new RequestType<Record<string, unknown>, QueryResultResponse, void>('query/fetch', ParameterStructures.byPosition);
const RPC_QUERY_COMPLETE = new RequestType<Record<string, unknown>, QueryCompleteResponse, void>('query/complete', ParameterStructures.byPosition);
const RPC_QUERY_HISTORY_LIST = new RequestType<Record<string, unknown>, QueryHistoryListResponse, void>('query/history/list', ParameterStructures.byPosition);
const RPC_QUERY_HISTORY_DELETE = new RequestType<Record<string, unknown>, QueryHistoryDeleteResponse, void>('query/history/delete', ParameterStructures.byPosition);
const RPC_QUERY_EXPORT = new RequestType<Record<string, unknown>, QueryExportResponse, void>('query/export', ParameterStructures.byPosition);
const RPC_QUERY_EXPLAIN = new RequestType<Record<string, unknown>, QueryExplainResponse, void>('query/explain', ParameterStructures.byPosition);

export class DaemonClient implements vscode.Disposable {
    private static readonly STARTUP_TIMEOUT_MS = 30_000;
    private static readonly CONNECT_TIMEOUT_MS = 15_000;

    private process: ChildProcess | null = null;
    private connection: MessageConnection | null = null;
    private connectingPromise: Promise<void> | null = null;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    private pendingNotificationHandlers: Array<{ method: string; handler: (...args: any[]) => void }> = [];
    private _disposed = false;
    private _state: DaemonState = 'stopped';
    private _heartbeatTimer: ReturnType<typeof setInterval> | undefined;
    private _heartbeatFailures = 0;
    private static readonly HEARTBEAT_INTERVAL_MS = 30_000;
    private static readonly HEARTBEAT_MAX_FAILURES = 3;

    private readonly _onDidChangeState = new vscode.EventEmitter<DaemonState>();
    readonly onDidChangeState = this._onDidChangeState.event;

    private readonly _onDidReconnect = new vscode.EventEmitter<void>();
    readonly onDidReconnect = this._onDidReconnect.event;

    private extensionPath: string;
    /** Temp directory holding shadow-copied daemon binaries (debug mode only). */
    private shadowCopyDir: string | null = null;

    constructor(extensionPath: string, private readonly log: vscode.LogOutputChannel) {
        this.extensionPath = extensionPath;
    }

    private setState(state: DaemonState): void {
        if (this._state === state) return;
        this._state = state;
        this._onDidChangeState.fire(state);
    }

    get state(): DaemonState { return this._state; }

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

        // 2. Debug build output (F5 development — extensionPath is src/PPDS.Extension/)
        //    Shadow-copy to a temp directory so dotnet build can overwrite the originals
        //    while the daemon holds locks on the copies.
        const debugDir = path.join(
            this.extensionPath, '..', 'PPDS.Cli', 'bin', 'Debug', 'net8.0'
        );
        const debugBinary = path.join(debugDir, `ppds${ext}`);
        try {
            fs.accessSync(debugBinary, fs.constants.X_OK);
            const shadowDir = this.shadowCopyDebugBuild(debugDir);
            const shadowBinary = path.join(shadowDir, `ppds${ext}`);
            this.log.info(`Using shadow-copied debug build: ${shadowBinary} (source: ${debugDir})`);
            return shadowBinary;
        } catch { /* no debug build */ }

        // 3. PATH fallback
        return 'ppds';
    }

    /**
     * Copies the debug build output to a temp directory so the daemon runs from
     * copies while dotnet build can freely overwrite the originals.
     */
    private shadowCopyDebugBuild(sourceDir: string): string {
        // Reuse existing shadow copy if it still exists
        if (this.shadowCopyDir && fs.existsSync(this.shadowCopyDir)) {
            fs.rmSync(this.shadowCopyDir, { recursive: true, force: true });
        }

        const tempDir = path.join(os.tmpdir(), `ppds-daemon-${Date.now()}`);
        fs.mkdirSync(tempDir, { recursive: true });

        // Copy all files from the build output (shallow — no subdirectories needed)
        for (const entry of fs.readdirSync(sourceDir, { withFileTypes: true })) {
            if (entry.isFile()) {
                fs.copyFileSync(
                    path.join(sourceDir, entry.name),
                    path.join(tempDir, entry.name)
                );
            }
        }

        this.shadowCopyDir = tempDir;
        return tempDir;
    }

    /**
     * Removes the shadow-copy temp directory if it exists.
     */
    private cleanupShadowCopy(): void {
        if (this.shadowCopyDir) {
            try {
                fs.rmSync(this.shadowCopyDir, { recursive: true, force: true });
            } catch {
                // Best-effort cleanup — temp dir will be reclaimed by OS eventually
            }
            this.shadowCopyDir = null;
        }
    }

    /**
     * Starts the daemon process and establishes JSON-RPC connection
     */
    async start(): Promise<void> {
        if (this.connection) {
            return; // Already running
        }

        this.setState('starting');

        const ppdsPath = this.resolvePpdsPath();
        this.log.info(`Starting ppds serve daemon (${ppdsPath})...`);

        // Spawn the daemon process
        this.process = spawn(ppdsPath, ['serve'], {
            stdio: ['pipe', 'pipe', 'pipe']
        });

        if (!this.process.stdout || !this.process.stdin) {
            throw new Error('Failed to create daemon process streams');
        }

        // Log stderr with parsed log levels
        this.process.stderr?.on('data', (data: Buffer) => {
            const text = data.toString().trimEnd();
            for (const line of text.split('\n')) {
                const trimmed = line.trimEnd();
                if (!trimmed) continue;
                const level = parseDaemonLogLevel(trimmed);
                this.log[level](`[daemon] ${trimmed}`);
            }
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

        const onStartupExit = (code: number | null): void => {
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
            this.setState('error');
            throw err;
        } finally {
            clearTimeout(timeoutId);
        }

        // Handshake succeeded — remove the startup exit listener to prevent
        // unhandled rejections, then install a post-startup exit listener for
        // auto-reconnect cleanup.
        // Guard: if the process exited in the narrow window between the handshake
        // resolving and this line, onStartupExit has already set this.process to null.
        // In that case we skip listener cleanup (nothing to remove) and skip the
        // post-startup exit listener (process is already gone).
        startupExitReject = null;
        if (this.process) {
            this.process.removeListener('exit', onStartupExit);

            this.process.on('exit', (code: number | null) => {
                this.log.warn(`Daemon exited with code ${code}`);
                this.connection = null;
                this.process = null;
                this.stopHeartbeat();
                this.setState('error');
            });
        }

        this.log.info('Daemon connection established');
        this.setState('ready');
        this.startHeartbeat();
    }

    // ── Auth methods ────────────────────────────────────────────────────────

    /**
     * Lists all authentication profiles.
     */
    async authList(): Promise<AuthListResponse> {
        await this.ensureConnected();

        this.log.debug('Calling auth/list...');
        const result = await this.connection!.sendRequest<AuthListResponse>('auth/list');

        // Defensive: older daemon versions (pre-SystemTextJsonFormatter) return PascalCase keys
        const raw = result as unknown as Record<string, unknown>;
        if (!result.profiles && raw['Profiles']) {
            result.profiles = raw['Profiles'] as AuthListResponse['profiles'];
            result.activeProfile = (raw['ActiveProfile'] as string | undefined) ?? result.activeProfile;
            result.activeProfileIndex = (raw['ActiveProfileIndex'] as number | undefined) ?? result.activeProfileIndex;
        }

        this.log.debug(`Got ${result.profiles?.length ?? 0} profiles`);
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
     * Lists available Dataverse environments (discovered + configured), optionally filtered.
     */
    async envList(filter?: string, forceRefresh?: boolean): Promise<EnvListResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (filter !== undefined) params.filter = filter;
        if (forceRefresh) params.forceRefresh = true;
        this.log.debug(`Calling env/list${filter ? ` with filter="${filter}"` : ''}${forceRefresh ? ' (force refresh)' : ''}...`);
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

    /**
     * Removes a configured environment from environments.json.
     */
    async envConfigRemove(environmentUrl: string): Promise<EnvConfigRemoveResponse> {
        await this.ensureConnected();

        this.log.debug(`Calling env/config/remove for "${environmentUrl}"...`);
        const result = await this.connection!.sendRequest<EnvConfigRemoveResponse>('env/config/remove', { environmentUrl });
        this.log.debug(`Removed: ${result.removed}`);

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
        dmlSafety?: { isConfirmed?: boolean; isDryRun?: boolean; noLimit?: boolean; rowCap?: number };
    }, token?: CancellationToken): Promise<QueryResultResponse> {
        await this.ensureConnected();

        this.log.info(`Calling query/sql: ${params.sql.substring(0, 100)}...`);
        const result = token
            ? await this.connection!.sendRequest(RPC_QUERY_SQL, params, token)
            : await this.connection!.sendRequest(RPC_QUERY_SQL, params);
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
            ? await this.connection!.sendRequest(RPC_QUERY_FETCH, params, token)
            : await this.connection!.sendRequest(RPC_QUERY_FETCH, params);
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
        return this.sendRequestQuiet(RPC_QUERY_COMPLETE, params);
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
        const result = await this.connection!.sendRequest(RPC_QUERY_HISTORY_LIST, params);
        this.log.debug(`Got ${result.entries.length} history entries`);
        return result;
    }

    /**
     * Deletes a query history entry.
     */
    async queryHistoryDelete(id: string): Promise<QueryHistoryDeleteResponse> {
        await this.ensureConnected();
        this.log.debug(`Calling query/history/delete for "${id}"...`);
        const result = await this.connection!.sendRequest(RPC_QUERY_HISTORY_DELETE, { id });
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
        fetchXml?: string;
        environmentUrl?: string;
        format?: string;
        includeHeaders?: boolean;
        top?: number;
    }): Promise<QueryExportResponse> {
        await this.ensureConnected();
        this.log.info(`Calling query/export...`);
        const result = await this.connection!.sendRequest(RPC_QUERY_EXPORT, params);
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
        const result = await this.connection!.sendRequest(RPC_QUERY_EXPLAIN, params);
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
    async solutionsList(filter?: string, includeManaged?: boolean, environmentUrl?: string, includeInternal?: boolean): Promise<SolutionsListResponse> {
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
        if (includeInternal !== undefined) {
            params.includeInternal = includeInternal;
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

    // ── Import Jobs ─────────────────────────────────────────────────────────

    async importJobsList(top?: number, environmentUrl?: string): Promise<ImportJobsListResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (top !== undefined) params.top = top;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info('Calling importJobs/list...');
        const result = await this.connection!.sendRequest<ImportJobsListResponse>('importJobs/list', params);
        this.log.debug(`Got ${result.jobs.length} import jobs`);

        return result;
    }

    async importJobsGet(id: string, environmentUrl?: string): Promise<ImportJobsGetResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { id };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling importJobs/get for ${id}...`);
        const result = await this.connection!.sendRequest<ImportJobsGetResponse>('importJobs/get', params);
        this.log.debug(`Got import job detail: ${result.job.solutionName}`);

        return result;
    }

    // ── Plugin Traces ─────────────────────────────────────────────────────────
    // ── Connection References ───────────────────────────────────────────────

    async connectionReferencesList(solutionId?: string, environmentUrl?: string, includeInactive = false): Promise<ConnectionReferencesListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (solutionId !== undefined) params.solutionId = solutionId;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        if (includeInactive) params.includeInactive = true;
        this.log.info('Calling connectionReferences/list...');
        const result = await this.connection!.sendRequest<ConnectionReferencesListResponse>('connectionReferences/list', params);
        this.log.debug(`Got ${result.references.length} connection references`);
        return result;
    }

    async connectionReferencesGet(logicalName: string, environmentUrl?: string): Promise<ConnectionReferencesGetResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { logicalName };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling connectionReferences/get for ${logicalName}...`);
        return await this.connection!.sendRequest<ConnectionReferencesGetResponse>('connectionReferences/get', params);
    }

    async connectionReferencesAnalyze(environmentUrl?: string): Promise<ConnectionReferencesAnalyzeResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling connectionReferences/analyze...');
        return await this.connection!.sendRequest<ConnectionReferencesAnalyzeResponse>('connectionReferences/analyze', params);
    }

    // ── Environment Variables ───────────────────────────────────────────────

    async environmentVariablesList(solutionId?: string, environmentUrl?: string, includeInactive?: boolean): Promise<EnvironmentVariablesListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (solutionId !== undefined) params.solutionId = solutionId;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        if (includeInactive !== undefined) params.includeInactive = includeInactive;
        this.log.info('Calling environmentVariables/list...');
        const result = await this.connection!.sendRequest<EnvironmentVariablesListResponse>('environmentVariables/list', params);
        this.log.debug(`Got ${result.variables.length} environment variables`);
        return result;
    }

    async environmentVariablesSyncDeploymentSettings(solutionId: string, filePath: string, environmentUrl?: string, token?: CancellationToken): Promise<EnvironmentVariablesSyncDeploymentSettingsResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { solutionId, filePath };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling environmentVariables/syncDeploymentSettings...');
        return token
            ? await this.connection!.sendRequest<EnvironmentVariablesSyncDeploymentSettingsResponse>('environmentVariables/syncDeploymentSettings', params, token)
            : await this.connection!.sendRequest<EnvironmentVariablesSyncDeploymentSettingsResponse>('environmentVariables/syncDeploymentSettings', params);
    }

    async environmentVariablesGet(schemaName: string, environmentUrl?: string): Promise<EnvironmentVariablesGetResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { schemaName };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling environmentVariables/get for ${schemaName}...`);
        return await this.connection!.sendRequest<EnvironmentVariablesGetResponse>('environmentVariables/get', params);
    }

    async environmentVariablesSet(schemaName: string, value: string, environmentUrl?: string): Promise<EnvironmentVariablesSetResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { schemaName, value };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling environmentVariables/set for ${schemaName}...`);
        return await this.connection!.sendRequest<EnvironmentVariablesSetResponse>('environmentVariables/set', params);
    }

    // ── Web Resources ──────────────────────────────────────────────────────

    async webResourcesList(
        solutionId?: string,
        textOnly = true,
        environmentUrl?: string,
    ): Promise<{ resources: WebResourceInfoDto[]; totalCount: number; filtersApplied?: string[] }> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { textOnly };
        if (solutionId !== undefined) params.solutionId = solutionId;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info('Calling webResources/list...');
        const result = await this.connection!.sendRequest<{ resources: WebResourceInfoDto[]; totalCount: number; filtersApplied?: string[] }>('webResources/list', params);
        this.log.debug(`Got ${result.resources.length} of ${result.totalCount} web resources`);

        return result;
    }

    async webResourcesGet(
        id: string,
        published = false,
        environmentUrl?: string,
    ): Promise<{ resource: WebResourceDetailDto | null }> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { id, published };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling webResources/get for ${id}...`);
        const result = await this.connection!.sendRequest<{ resource: WebResourceDetailDto | null }>('webResources/get', params);
        this.log.debug(`Got web resource detail: ${result.resource?.name ?? 'null'}`);

        return result;
    }

    async webResourcesGetModifiedOn(
        id: string,
        environmentUrl?: string,
    ): Promise<{ modifiedOn: string | null }> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { id };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling webResources/getModifiedOn for ${id}...`);
        const result = await this.connection!.sendRequest<{ modifiedOn: string | null }>('webResources/getModifiedOn', params);
        this.log.debug(`Got modifiedOn: ${result.modifiedOn ?? 'null'}`);

        return result;
    }

    async webResourcesUpdate(
        id: string,
        content: string,
        environmentUrl?: string,
    ): Promise<{ success: boolean }> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { id, content };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling webResources/update for ${id}...`);
        const result = await this.connection!.sendRequest<{ success: boolean }>('webResources/update', params);
        this.log.debug(`Update result: ${result.success}`);

        return result;
    }

    async webResourcesPublish(
        ids: string[],
        environmentUrl?: string,
    ): Promise<{ publishedCount: number }> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { ids };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling webResources/publish for ${ids.length} resources...`);
        const result = await this.connection!.sendRequest<{ publishedCount: number }>('webResources/publish', params);
        this.log.debug(`Published ${result.publishedCount} web resources`);

        return result;
    }

    async webResourcesPublishAll(
        environmentUrl?: string,
    ): Promise<{ success: boolean }> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info('Calling webResources/publishAll...');
        const result = await this.connection!.sendRequest<{ success: boolean }>('webResources/publishAll', params);
        this.log.debug(`PublishAll result: ${result.success}`);

        return result;
    }

    // ── Schema ──────────────────────────────────────────────────────────────

    async pluginTracesList(filter?: TraceFilterDto, top?: number, environmentUrl?: string): Promise<PluginTracesListResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (filter !== undefined) params.filter = filter;
        if (top !== undefined) params.top = top;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info('Calling pluginTraces/list...');
        const result = await this.connection!.sendRequest<PluginTracesListResponse>('pluginTraces/list', params);
        this.log.debug(`Got ${result.traces.length} plugin traces`);

        return result;
    }

    async pluginTracesGet(id: string, environmentUrl?: string): Promise<PluginTracesGetResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { id };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling pluginTraces/get for ${id}...`);
        const result = await this.connection!.sendRequest<PluginTracesGetResponse>('pluginTraces/get', params);
        this.log.debug(`Got plugin trace detail: ${result.trace.typeName}`);

        return result;
    }

    async pluginTracesTimeline(correlationId: string, environmentUrl?: string): Promise<PluginTracesTimelineResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { correlationId };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling pluginTraces/timeline for ${correlationId}...`);
        const result = await this.connection!.sendRequest<PluginTracesTimelineResponse>('pluginTraces/timeline', params);
        this.log.debug(`Got ${result.nodes.length} timeline nodes`);

        return result;
    }

    async pluginTracesDelete(ids?: string[], olderThanDays?: number, environmentUrl?: string): Promise<PluginTracesDeleteResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (ids !== undefined) params.ids = ids;
        if (olderThanDays !== undefined) params.olderThanDays = olderThanDays;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info('Calling pluginTraces/delete...');
        const result = await this.connection!.sendRequest<PluginTracesDeleteResponse>('pluginTraces/delete', params);
        this.log.debug(`Deleted ${result.deletedCount} plugin traces`);

        return result;
    }

    async pluginTracesTraceLevel(environmentUrl?: string): Promise<PluginTracesTraceLevelResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info('Calling pluginTraces/traceLevel...');
        const result = await this.connection!.sendRequest<PluginTracesTraceLevelResponse>('pluginTraces/traceLevel', params);
        this.log.debug(`Trace level: ${result.level} (${result.levelValue})`);

        return result;
    }

    async pluginTracesSetTraceLevel(level: string, environmentUrl?: string): Promise<PluginTracesSetTraceLevelResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { level };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.info(`Calling pluginTraces/setTraceLevel with level=${level}...`);
        const result = await this.connection!.sendRequest<PluginTracesSetTraceLevelResponse>('pluginTraces/setTraceLevel', params);
        this.log.debug(`Set trace level success: ${result.success}`);

        return result;
    }

    // ── Metadata ────────────────────────────────────────────────────────────

    async metadataEntities(environmentUrl?: string, includeIntersect = false): Promise<MetadataEntitiesResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { includeIntersect };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.debug('Calling metadata/entities...');
        const result = await this.connection!.sendRequest<MetadataEntitiesResponse>(
            'metadata/entities',
            params
        );
        this.log.debug(`Got ${result.entities.length} entities`);

        return result;
    }

    async metadataGlobalOptionSets(environmentUrl?: string): Promise<MetadataGlobalOptionSetsResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.debug('Calling metadata/globalOptionSets...');
        const result = await this.connection!.sendRequest<MetadataGlobalOptionSetsResponse>(
            'metadata/globalOptionSets',
            params
        );
        this.log.debug(`Got ${result.optionSets.length} global option sets`);

        return result;
    }

    async metadataGlobalOptionSet(name: string, environmentUrl?: string): Promise<MetadataGlobalOptionSetDetailResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { name };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.debug(`Calling metadata/globalOptionSet for "${name}"...`);
        const result = await this.connection!.sendRequest<MetadataGlobalOptionSetDetailResponse>(
            'metadata/globalOptionSet',
            params
        );
        this.log.debug(`Got global option set "${name}"`);

        return result;
    }

    async metadataEntity(
        logicalName: string,
        includeGlobalOptionSets = false,
        environmentUrl?: string,
    ): Promise<MetadataEntityResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = { logicalName, includeGlobalOptionSets };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

        this.log.debug(`Calling metadata/entity for "${logicalName}"...`);
        const result = await this.connection!.sendRequest<MetadataEntityResponse>(
            'metadata/entity',
            params
        );
        this.log.debug(`Got entity "${logicalName}" with ${result.entity.attributes.length} attributes`);

        return result;
    }

    // ── Metadata Authoring ─────────────────────────────────────────────────

    async metadataCreateTable(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/createTable...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/createTable', rpcParams);
    }

    async metadataUpdateTable(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/updateTable...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/updateTable', rpcParams);
    }

    async metadataDeleteTable(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataDeleteResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/deleteTable...');
        return await this.connection!.sendRequest<MetadataDeleteResult>('metadata/deleteTable', rpcParams);
    }

    async metadataCreateColumn(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/createColumn...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/createColumn', rpcParams);
    }

    async metadataUpdateColumn(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/updateColumn...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/updateColumn', rpcParams);
    }

    async metadataDeleteColumn(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataDeleteResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/deleteColumn...');
        return await this.connection!.sendRequest<MetadataDeleteResult>('metadata/deleteColumn', rpcParams);
    }

    async metadataCreateOneToMany(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/createOneToMany...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/createOneToMany', rpcParams);
    }

    async metadataCreateManyToMany(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/createManyToMany...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/createManyToMany', rpcParams);
    }

    async metadataDeleteRelationship(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataDeleteResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/deleteRelationship...');
        return await this.connection!.sendRequest<MetadataDeleteResult>('metadata/deleteRelationship', rpcParams);
    }

    async metadataCreateGlobalChoice(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/createGlobalChoice...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/createGlobalChoice', rpcParams);
    }

    async metadataDeleteGlobalChoice(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataDeleteResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/deleteGlobalChoice...');
        return await this.connection!.sendRequest<MetadataDeleteResult>('metadata/deleteGlobalChoice', rpcParams);
    }

    async metadataCreateKey(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataAuthoringResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/createKey...');
        return await this.connection!.sendRequest<MetadataAuthoringResult>('metadata/createKey', rpcParams);
    }

    async metadataDeleteKey(params: Record<string, unknown>, environmentUrl?: string): Promise<MetadataDeleteResult> {
        await this.ensureConnected();
        const rpcParams: Record<string, unknown> = { ...params };
        if (environmentUrl !== undefined) rpcParams.environmentUrl = environmentUrl;
        this.log.info('Calling metadata/deleteKey...');
        return await this.connection!.sendRequest<MetadataDeleteResult>('metadata/deleteKey', rpcParams);
    }

    // ── Plugins ─────────────────────────────────────────────────────────────

    async pluginsList(environmentUrl?: string): Promise<PluginsListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling plugins/list...');
        return await this.connection!.sendRequest<PluginsListResponse>('plugins/list', params);
    }

    async pluginsGet(type: string, id: string, environmentUrl?: string): Promise<PluginsGetResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { type, id };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/get for ${type} ${id}...`);
        return await this.connection!.sendRequest<PluginsGetResponse>('plugins/get', params);
    }

    async pluginsMessages(filter?: string, environmentUrl?: string): Promise<PluginsMessagesResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (filter !== undefined) params.filter = filter;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling plugins/messages...');
        return await this.connection!.sendRequest<PluginsMessagesResponse>('plugins/messages', params);
    }

    async pluginsEntityAttributes(entityLogicalName: string, environmentUrl?: string): Promise<PluginsEntityAttributesResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { entityLogicalName };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/entityAttributes for ${entityLogicalName}...`);
        return await this.connection!.sendRequest<PluginsEntityAttributesResponse>('plugins/entityAttributes', params);
    }

    async pluginsToggleStep(id: string, enabled: boolean, environmentUrl?: string): Promise<PluginsToggleStepResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id, enabled };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/toggleStep for ${id} enabled=${enabled}...`);
        return await this.connection!.sendRequest<PluginsToggleStepResponse>('plugins/toggleStep', params);
    }

    async pluginsRegisterAssembly(content: string, solutionName?: string, environmentUrl?: string): Promise<PluginsRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { content };
        if (solutionName !== undefined) params.solutionName = solutionName;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling plugins/registerAssembly...');
        return await this.connection!.sendRequest<PluginsRegisterResponse>('plugins/registerAssembly', params);
    }

    async pluginsRegisterPackage(content: string, solutionName?: string, environmentUrl?: string): Promise<PluginsRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { content };
        if (solutionName !== undefined) params.solutionName = solutionName;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling plugins/registerPackage...');
        return await this.connection!.sendRequest<PluginsRegisterResponse>('plugins/registerPackage', params);
    }

    async pluginsRegisterStep(config: Record<string, unknown>, environmentUrl?: string): Promise<PluginsRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { ...config };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling plugins/registerStep...');
        return await this.connection!.sendRequest<PluginsRegisterResponse>('plugins/registerStep', params);
    }

    async pluginsRegisterImage(config: Record<string, unknown>, environmentUrl?: string): Promise<PluginsRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { ...config };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling plugins/registerImage...');
        return await this.connection!.sendRequest<PluginsRegisterResponse>('plugins/registerImage', params);
    }

    async pluginsUpdateStep(id: string, updates: Record<string, unknown>, environmentUrl?: string): Promise<PluginsUpdateResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id, ...updates };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/updateStep for ${id}...`);
        return await this.connection!.sendRequest<PluginsUpdateResponse>('plugins/updateStep', params);
    }

    async pluginsUpdateImage(id: string, updates: Record<string, unknown>, environmentUrl?: string): Promise<PluginsUpdateResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id, ...updates };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/updateImage for ${id}...`);
        return await this.connection!.sendRequest<PluginsUpdateResponse>('plugins/updateImage', params);
    }

    async pluginsUnregister(type: string, id: string, force?: boolean, environmentUrl?: string): Promise<PluginsUnregisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { type, id };
        if (force !== undefined) params.force = force;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/unregister for ${type} ${id}...`);
        return await this.connection!.sendRequest<PluginsUnregisterResponse>('plugins/unregister', params);
    }

    async pluginsDownloadBinary(type: string, id: string, environmentUrl?: string): Promise<PluginsDownloadResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { type, id };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling plugins/downloadBinary for ${type} ${id}...`);
        return await this.connection!.sendRequest<PluginsDownloadResponse>('plugins/downloadBinary', params);
    }

    // ── Service Endpoints ────────────────────────────────────────────────────

    async serviceEndpointsList(environmentUrl?: string): Promise<ServiceEndpointsListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling serviceEndpoints/list...');
        return await this.connection!.sendRequest<ServiceEndpointsListResponse>('serviceEndpoints/list', params);
    }

    async serviceEndpointsGet(id: string, environmentUrl?: string): Promise<ServiceEndpointsGetResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling serviceEndpoints/get for ${id}...`);
        return await this.connection!.sendRequest<ServiceEndpointsGetResponse>('serviceEndpoints/get', params);
    }

    async serviceEndpointsRegister(fields: Record<string, unknown>, environmentUrl?: string): Promise<ServiceEndpointsRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { ...fields };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling serviceEndpoints/register...');
        return await this.connection!.sendRequest<ServiceEndpointsRegisterResponse>('serviceEndpoints/register', params);
    }

    async serviceEndpointsUpdate(id: string, fields: Record<string, unknown>, environmentUrl?: string): Promise<ServiceEndpointsRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id, ...fields };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling serviceEndpoints/update for ${id}...`);
        return await this.connection!.sendRequest<ServiceEndpointsRegisterResponse>('serviceEndpoints/update', params);
    }

    async serviceEndpointsUnregister(id: string, force?: boolean, environmentUrl?: string): Promise<{ success: boolean }> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id };
        if (force !== undefined) params.force = force;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling serviceEndpoints/unregister for ${id}...`);
        return await this.connection!.sendRequest<{ success: boolean }>('serviceEndpoints/unregister', params);
    }

    // ── Custom APIs ──────────────────────────────────────────────────────────

    async customApisList(environmentUrl?: string): Promise<CustomApisListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling customApis/list...');
        return await this.connection!.sendRequest<CustomApisListResponse>('customApis/list', params);
    }

    async customApisGet(uniqueNameOrId: string, environmentUrl?: string): Promise<CustomApisGetResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { uniqueNameOrId };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling customApis/get for ${uniqueNameOrId}...`);
        return await this.connection!.sendRequest<CustomApisGetResponse>('customApis/get', params);
    }

    async customApisRegister(fields: Record<string, unknown>, environmentUrl?: string): Promise<CustomApisRegisterResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { ...fields };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling customApis/register...');
        return await this.connection!.sendRequest<CustomApisRegisterResponse>('customApis/register', params);
    }

    async customApisUpdate(id: string, fields: Record<string, unknown>, environmentUrl?: string): Promise<{ success: boolean }> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id, ...fields };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling customApis/update for ${id}...`);
        return await this.connection!.sendRequest<{ success: boolean }>('customApis/update', params);
    }

    async customApisUnregister(id: string, force?: boolean, environmentUrl?: string): Promise<{ success: boolean }> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { id };
        if (force !== undefined) params.force = force;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling customApis/unregister for ${id}...`);
        return await this.connection!.sendRequest<{ success: boolean }>('customApis/unregister', params);
    }

    async customApisAddParameter(apiId: string, fields: Record<string, unknown>, environmentUrl?: string): Promise<{ id: string }> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { apiId, ...fields };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling customApis/addParameter for api ${apiId}...`);
        return await this.connection!.sendRequest<{ id: string }>('customApis/addParameter', params);
    }

    async customApisUpdateParameter(parameterId: string, displayName?: string, description?: string, environmentUrl?: string): Promise<{ success: boolean }> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { parameterId };
        if (displayName !== undefined) params.displayName = displayName;
        if (description !== undefined) params.description = description;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling customApis/updateParameter for ${parameterId}...`);
        return await this.connection!.sendRequest<{ success: boolean }>('customApis/updateParameter', params);
    }

    async customApisRemoveParameter(parameterId: string, environmentUrl?: string): Promise<{ success: boolean }> {
        await this.ensureConnected();
        const params: Record<string, unknown> = { parameterId };
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info(`Calling customApis/removeParameter for ${parameterId}...`);
        return await this.connection!.sendRequest<{ success: boolean }>('customApis/removeParameter', params);
    }

    // ── Data Providers ───────────────────────────────────────────────────────

    async dataProvidersList(dataSourceId?: string, environmentUrl?: string): Promise<DataProvidersListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (dataSourceId !== undefined) params.dataSourceId = dataSourceId;
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling dataProviders/list...');
        return await this.connection!.sendRequest<DataProvidersListResponse>('dataProviders/list', params);
    }

    async dataSourcesList(environmentUrl?: string): Promise<DataSourcesListResponse> {
        await this.ensureConnected();
        const params: Record<string, unknown> = {};
        if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;
        this.log.info('Calling dataSources/list...');
        return await this.connection!.sendRequest<DataSourcesListResponse>('dataSources/list', params);
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

    // ── Diagnostic accessors ────────────────────────────────────────────────

    /**
     * Returns true if the daemon process is running and the JSON-RPC
     * connection is established. Used by diagnostic commands.
     */
    isReady(): boolean {
        return this.connection !== null && this.process !== null;
    }

    /**
     * Returns the PID of the daemon child process, or null if not running.
     * Used by diagnostic commands for process inspection.
     */
    getProcessId(): number | null {
        return this.process?.pid ?? null;
    }

    // ── Connection management ───────────────────────────────────────────────

    /**
     * Sends an RPC request without logging, for high-frequency calls such as
     * IntelliSense completions that would otherwise flood the output channel.
     */
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    private async sendRequestQuiet<T>(method: string | RequestType<any, T, any>, params?: unknown): Promise<T> {
        await this.ensureConnected();
        // eslint-disable-next-line @typescript-eslint/no-explicit-any, @typescript-eslint/no-unsafe-argument -- JSON-RPC accepts string | RequestType
        return this.connection!.sendRequest(method as any, params);
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
            const wasConnectedBefore = this._state === 'error';
            this.setState('reconnecting');
            this.connectingPromise = this.start().then(() => {
                if (wasConnectedBefore) {
                    this._onDidReconnect.fire();
                }
            }).finally(() => {
                this.connectingPromise = null;
            });
        }
        await withRpcTimeout(this.connectingPromise, DaemonClient.CONNECT_TIMEOUT_MS, 'ensureConnected');
    }

    private startHeartbeat(): void {
        this.stopHeartbeat();
        this._heartbeatTimer = setInterval(() => {
            if (!this.connection || this._disposed) {
                this.stopHeartbeat();
                return;
            }
            // Use auth/list as heartbeat ping
            // TODO: Consider a lightweight health/ping endpoint
            this.connection.sendRequest('auth/list').then(() => {
                this._heartbeatFailures = 0;
            }).catch((err: unknown) => {
                this._heartbeatFailures++;
                this.log.debug(`Heartbeat error: ${err instanceof Error ? err.message : String(err)}`);
                this.log.warn(`Heartbeat failed (${this._heartbeatFailures}/${DaemonClient.HEARTBEAT_MAX_FAILURES}) — daemon may be unresponsive`);
                if (this._heartbeatFailures >= DaemonClient.HEARTBEAT_MAX_FAILURES) {
                    this.log.warn('Max consecutive heartbeat failures reached — killing daemon');
                    this.connection?.dispose();
                    this.connection = null;
                    this.process?.kill();
                    this.process = null;
                    this.stopHeartbeat();
                    this.setState('error');
                }
            });
        }, DaemonClient.HEARTBEAT_INTERVAL_MS);
    }

    private stopHeartbeat(): void {
        if (this._heartbeatTimer) {
            clearInterval(this._heartbeatTimer);
            this._heartbeatTimer = undefined;
        }
        this._heartbeatFailures = 0;
    }

    async restart(): Promise<void> {
        if (this._disposed) throw new Error('DaemonClient is disposed');
        this.stopHeartbeat();
        if (this.connection) {
            this.connection.dispose();
            this.connection = null;
        }
        if (this.process) {
            this.process.kill();
            this.process = null;
        }
        await this.start();
    }

    /**
     * Stops the daemon and cleans up resources
     */
    dispose(): void {
        this.log.info('Disposing daemon client...');
        this._disposed = true;
        this.connectingPromise = null;
        this.stopHeartbeat();

        if (this.connection) {
            this.connection.dispose();
            this.connection = null;
        }

        if (this.process) {
            this.process.kill();
            this.process = null;
        }

        this.cleanupShadowCopy();

        // Dispose EventEmitters LAST
        this.setState('stopped');
        this._onDidChangeState.dispose();
        this._onDidReconnect.dispose();
    }
}

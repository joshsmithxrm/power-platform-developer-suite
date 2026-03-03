import * as vscode from 'vscode';
import { spawn, ChildProcess } from 'child_process';
import {
    createMessageConnection,
    MessageConnection,
    StreamMessageReader,
    StreamMessageWriter
} from 'vscode-jsonrpc/node';
import type {
    AuthListResponse,
    AuthWhoResponse,
    AuthSelectResponse,
    EnvListResponse,
    EnvSelectResponse,
    EnvConfigGetResponse,
    EnvConfigSetResponse,
    QueryResultResponse,
    QueryCompleteResponse,
    QueryHistoryListResponse,
    QueryHistoryDeleteResponse,
    ProfilesInvalidateResponse,
    SolutionsListResponse,
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
    QueryResultResponse,
    QueryCompleteResponse,
    QueryHistoryListResponse,
    QueryHistoryDeleteResponse,
    ProfilesInvalidateResponse,
    SolutionsListResponse,
} from './types.js';

/**
 * Client for communicating with the ppds serve daemon via JSON-RPC.
 *
 * All RPC methods call ensureConnected() first, which starts the daemon
 * process if it is not already running. If the daemon dies, the next RPC
 * call will automatically restart it (auto-reconnect).
 */
export class DaemonClient implements vscode.Disposable {
    private process: ChildProcess | null = null;
    private connection: MessageConnection | null = null;
    private outputChannel: vscode.OutputChannel;

    constructor() {
        this.outputChannel = vscode.window.createOutputChannel('PPDS Daemon');
    }

    /**
     * Starts the daemon process and establishes JSON-RPC connection
     */
    async start(): Promise<void> {
        if (this.connection) {
            return; // Already running
        }

        this.outputChannel.appendLine('Starting ppds serve daemon...');

        // Spawn the daemon process
        this.process = spawn('ppds', ['serve'], {
            stdio: ['pipe', 'pipe', 'pipe'],
            shell: true
        });

        if (!this.process.stdout || !this.process.stdin) {
            throw new Error('Failed to create daemon process streams');
        }

        // Log stderr for debugging
        this.process.stderr?.on('data', (data: Buffer) => {
            this.outputChannel.appendLine(`[daemon stderr] ${data.toString()}`);
        });

        this.process.on('error', (err) => {
            this.outputChannel.appendLine(`Daemon error: ${err.message}`);
            vscode.window.showErrorMessage(`PPDS daemon error: ${err.message}`);
        });

        this.process.on('exit', (code) => {
            this.outputChannel.appendLine(`Daemon exited with code ${code}`);
            this.connection = null;
            this.process = null;
        });

        // Create JSON-RPC connection over stdio
        const reader = new StreamMessageReader(this.process.stdout);
        const writer = new StreamMessageWriter(this.process.stdin);
        this.connection = createMessageConnection(reader, writer);

        // Start listening for messages
        this.connection.listen();

        this.outputChannel.appendLine('Daemon connection established');
    }

    // ── Auth methods ────────────────────────────────────────────────────────

    /**
     * Lists all authentication profiles.
     */
    async authList(): Promise<AuthListResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine('Calling auth/list...');
        const result = await this.connection!.sendRequest<AuthListResponse>('auth/list');
        this.outputChannel.appendLine(`Got ${result.profiles.length} profiles`);

        return result;
    }

    /**
     * Gets detailed information about the currently active profile.
     */
    async authWho(): Promise<AuthWhoResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine('Calling auth/who...');
        const result = await this.connection!.sendRequest<AuthWhoResponse>('auth/who');
        this.outputChannel.appendLine(`auth/who returned profile index ${result.index}`);

        return result;
    }

    /**
     * Selects (activates) an authentication profile by index or name.
     */
    async authSelect(params: { index?: number; name?: string }): Promise<AuthSelectResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine(`Calling auth/select with params: ${JSON.stringify(params)}...`);
        const result = await this.connection!.sendRequest<AuthSelectResponse>('auth/select', params);
        this.outputChannel.appendLine(`Selected profile: ${result.name ?? result.identity}`);

        return result;
    }

    // ── Environment methods ─────────────────────────────────────────────────

    /**
     * Lists available Dataverse environments, optionally filtered.
     */
    async envList(filter?: string): Promise<EnvListResponse> {
        await this.ensureConnected();

        const params = filter !== undefined ? { filter } : {};
        this.outputChannel.appendLine(`Calling env/list${filter ? ` with filter="${filter}"` : ''}...`);
        const result = await this.connection!.sendRequest<EnvListResponse>('env/list', params);
        this.outputChannel.appendLine(`Got ${result.environments.length} environments`);

        return result;
    }

    /**
     * Selects (activates) a Dataverse environment by URL or name.
     */
    async envSelect(environment: string): Promise<EnvSelectResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine(`Calling env/select for "${environment}"...`);
        const result = await this.connection!.sendRequest<EnvSelectResponse>('env/select', { environment });
        this.outputChannel.appendLine(`Selected environment: ${result.displayName} (${result.url})`);

        return result;
    }

    /**
     * Gets the configuration for a specific environment.
     */
    async envConfigGet(environmentUrl: string): Promise<EnvConfigGetResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine(`Calling env/config/get for "${environmentUrl}"...`);
        const result = await this.connection!.sendRequest<EnvConfigGetResponse>('env/config/get', { environmentUrl });
        this.outputChannel.appendLine(`Got config: label=${result.label ?? '(none)'}, type=${result.type ?? '(none)'}, color=${result.color ?? '(none)'}`);

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

        this.outputChannel.appendLine(`Calling env/config/set for "${params.environmentUrl}"...`);
        const result = await this.connection!.sendRequest<EnvConfigSetResponse>('env/config/set', params);
        this.outputChannel.appendLine(`Config saved: saved=${result.saved}`);

        return result;
    }

    // ── Query methods ───────────────────────────────────────────────────────

    /**
     * Executes a SQL query against the active Dataverse environment.
     */
    async querySql(params: {
        sql: string;
        top?: number;
        page?: number;
        pagingCookie?: string;
        count?: boolean;
        showFetchXml?: boolean;
        useTds?: boolean;
    }): Promise<QueryResultResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine(`Calling query/sql: ${params.sql.substring(0, 100)}...`);
        const result = await this.connection!.sendRequest<QueryResultResponse>('query/sql', params);
        this.outputChannel.appendLine(`Query returned ${result.count} records in ${result.executionTimeMs}ms`);

        return result;
    }

    /**
     * Executes a FetchXML query against the active Dataverse environment.
     */
    async queryFetch(params: {
        fetchXml: string;
        top?: number;
        page?: number;
        pagingCookie?: string;
        count?: boolean;
    }): Promise<QueryResultResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine('Calling query/fetch...');
        const result = await this.connection!.sendRequest<QueryResultResponse>('query/fetch', params);
        this.outputChannel.appendLine(`Query returned ${result.count} records in ${result.executionTimeMs}ms`);

        return result;
    }

    /**
     * Gets IntelliSense completion items for SQL/FetchXML.
     */
    async queryComplete(params: { sql: string; cursorOffset: number }): Promise<QueryCompleteResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine(`Calling query/complete at offset ${params.cursorOffset}...`);
        const result = await this.connection!.sendRequest<QueryCompleteResponse>('query/complete', params);
        this.outputChannel.appendLine(`Got ${result.items.length} completion items`);

        return result;
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
        this.outputChannel.appendLine(`Calling query/history/list...`);
        const result = await this.connection!.sendRequest<QueryHistoryListResponse>('query/history/list', params);
        this.outputChannel.appendLine(`Got ${result.entries.length} history entries`);
        return result;
    }

    /**
     * Deletes a query history entry.
     */
    async queryHistoryDelete(id: string): Promise<QueryHistoryDeleteResponse> {
        await this.ensureConnected();
        this.outputChannel.appendLine(`Calling query/history/delete for "${id}"...`);
        const result = await this.connection!.sendRequest<QueryHistoryDeleteResponse>('query/history/delete', { id });
        this.outputChannel.appendLine(`Deleted: ${result.deleted}`);
        return result;
    }

    // ── Profile management ──────────────────────────────────────────────────

    /**
     * Invalidates (clears cached tokens for) a profile.
     */
    async profilesInvalidate(profileName: string): Promise<ProfilesInvalidateResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine(`Calling profiles/invalidate for "${profileName}"...`);
        const result = await this.connection!.sendRequest<ProfilesInvalidateResponse>(
            'profiles/invalidate',
            { profileName }
        );
        this.outputChannel.appendLine(`Profile "${profileName}" invalidated: ${result.invalidated}`);

        return result;
    }

    // ── Solutions ───────────────────────────────────────────────────────────

    /**
     * Lists solutions in the active Dataverse environment.
     */
    async solutionsList(filter?: string, includeManaged?: boolean): Promise<SolutionsListResponse> {
        await this.ensureConnected();

        const params: Record<string, unknown> = {};
        if (filter !== undefined) {
            params.filter = filter;
        }
        if (includeManaged !== undefined) {
            params.includeManaged = includeManaged;
        }

        this.outputChannel.appendLine(`Calling solutions/list${filter ? ` with filter="${filter}"` : ''}...`);
        const result = await this.connection!.sendRequest<SolutionsListResponse>('solutions/list', params);
        this.outputChannel.appendLine(`Got ${result.solutions.length} solutions`);

        return result;
    }

    // ── Notifications ───────────────────────────────────────────────────────

    /**
     * Registers a handler for device code authentication notifications.
     * The daemon sends these when interactive browser-based auth is required.
     */
    onDeviceCode(handler: (params: { userCode: string; verificationUrl: string; message: string }) => void): void {
        if (!this.connection) {
            throw new Error('Cannot register notification handler: daemon is not connected. Call start() first.');
        }
        this.connection.onNotification('auth/deviceCode', handler);
        this.outputChannel.appendLine('Registered auth/deviceCode notification handler');
    }

    // ── Connection management ───────────────────────────────────────────────

    /**
     * Ensures the daemon is connected, starting it if necessary.
     * This provides auto-reconnect: if the daemon process dies, the next
     * RPC call will restart it since the exit handler sets connection to null.
     */
    private async ensureConnected(): Promise<void> {
        if (!this.connection) {
            await this.start();
        }
    }

    /**
     * Stops the daemon and cleans up resources
     */
    dispose(): void {
        this.outputChannel.appendLine('Disposing daemon client...');

        if (this.connection) {
            this.connection.dispose();
            this.connection = null;
        }

        if (this.process) {
            this.process.kill();
            this.process = null;
        }

        this.outputChannel.dispose();
    }
}

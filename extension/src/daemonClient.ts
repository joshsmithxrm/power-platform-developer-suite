import * as vscode from 'vscode';
import { spawn, ChildProcess } from 'child_process';
import {
    createMessageConnection,
    MessageConnection,
    StreamMessageReader,
    StreamMessageWriter
} from 'vscode-jsonrpc/node';

/**
 * Response from auth/list RPC method
 */
export interface AuthListResponse {
    activeProfile: string | null;
    activeProfileIndex: number | null;
    profiles: ProfileInfo[];
}

/**
 * Profile information from auth/list
 */
export interface ProfileInfo {
    index: number;
    name: string | null;
    identity: string;
    authMethod: string;
    cloud: string;
    environment: EnvironmentSummary | null;
    isActive: boolean;
    createdAt: string | null;
    lastUsedAt: string | null;
}

/**
 * Environment summary in profile
 */
export interface EnvironmentSummary {
    url: string;
    displayName: string;
}

/**
 * Client for communicating with the ppds serve daemon via JSON-RPC
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

    /**
     * Lists all authentication profiles
     */
    async listProfiles(): Promise<AuthListResponse> {
        await this.ensureConnected();

        this.outputChannel.appendLine('Calling auth/list...');
        const result = await this.connection!.sendRequest<AuthListResponse>('auth/list');
        this.outputChannel.appendLine(`Got ${result.profiles.length} profiles`);

        return result;
    }

    /**
     * Ensures the daemon is connected, starting it if necessary
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

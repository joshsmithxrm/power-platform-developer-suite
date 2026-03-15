import { ResponseError } from 'vscode-jsonrpc/node';
import * as vscode from 'vscode';

import type { DaemonClient } from '../daemonClient.js';

/**
 * Structured error codes from the daemon RPC layer.
 * These mirror ErrorCodes in PPDS.Cli.Infrastructure.Errors.ErrorCodes (C#).
 * The daemon sends errors as RpcErrorData in the ResponseError.data property.
 */
const AUTH_ERROR_CODES = new Set([
    'Auth.NoActiveProfile',
    'Auth.ProfileNotFound',
    'Auth.Expired',
    'Auth.InvalidCredentials',
    'Auth.InsufficientPermissions',
    'Auth.CertificateError',
]);

/**
 * Type guard for RPC error data payload sent by the daemon.
 * The daemon's RpcException stores a RpcErrorData object in ErrorData,
 * which arrives as the `data` property of a vscode-jsonrpc ResponseError.
 */
interface RpcErrorData {
    code: string;
    message: string;
    details?: string;
    target?: string;
}

function isRpcErrorData(value: unknown): value is RpcErrorData {
    return (
        typeof value === 'object' &&
        value !== null &&
        'code' in value &&
        typeof (value as RpcErrorData).code === 'string'
    );
}

/**
 * Detect authentication errors from daemon RPC responses.
 *
 * Prefers structured error codes from the daemon's RpcErrorData payload,
 * falling back to message heuristics for non-RPC errors (e.g., network
 * failures or errors from other sources).
 */
export function isAuthError(error: unknown): boolean {
    // Prefer structured error codes from daemon RPC
    if (error instanceof ResponseError) {
        const data: unknown = error.data;
        if (isRpcErrorData(data)) {
            return AUTH_ERROR_CODES.has(data.code);
        }
    }

    // Fallback for non-RPC errors (network errors, SDK exceptions, etc.)
    const msg = error instanceof Error ? error.message : String(error);
    const lower = msg.toLowerCase();
    return (
        lower.includes('unauthorized') ||
        /\b401\b/.test(lower) ||
        lower.includes('authentication failed') ||
        lower.includes('token expired') ||
        lower.includes('token has expired') ||
        lower.includes('invalid_grant') ||
        lower.includes('aadsts')
    );
}

/**
 * Shared auth-error retry handler for panels.
 * Shows a re-authentication prompt when an auth error is detected,
 * invalidates cached tokens, and retries the operation once.
 *
 * Note: DataverseNotebookController has different auth-error semantics
 * (no retry, shows "re-execute" message) and does NOT use this helper.
 *
 * @returns true if the retry succeeded (caller should return early),
 *          false otherwise (caller should show error to user)
 */
export async function handleAuthError(
    daemon: DaemonClient,
    error: unknown,
    isRetry: boolean,
    retry: () => Promise<void>,
): Promise<boolean> {
    if (!isAuthError(error) || isRetry) return false;

    const action = await vscode.window.showErrorMessage(
        'Session expired. Re-authenticate?',
        'Re-authenticate', 'Cancel',
    );
    if (action !== 'Re-authenticate') return false;

    try {
        const who = await daemon.authWho();
        const profileId = who.name ?? String(who.index);
        await daemon.profilesInvalidate(profileId);
    } catch {
        // If authWho fails, proceed with retry anyway
    }

    try {
        await retry();
        return true;
    } catch {
        return false;
    }
}

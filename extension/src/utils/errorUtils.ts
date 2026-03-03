import { ResponseError } from 'vscode-jsonrpc/node';

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

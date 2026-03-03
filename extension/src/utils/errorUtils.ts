/**
 * Detect authentication errors from daemon RPC responses.
 * Uses specific patterns to avoid false positives from field names
 * like "author" or "token" in query results.
 */
export function isAuthError(error: unknown): boolean {
    const msg = error instanceof Error ? error.message : String(error);
    const lower = msg.toLowerCase();
    return (
        lower.includes('unauthorized') ||
        lower.includes('401') ||
        lower.includes('authentication failed') ||
        lower.includes('token expired') ||
        lower.includes('token has expired') ||
        lower.includes('invalid_grant') ||
        lower.includes('aadsts') ||  // Azure AD error codes
        lower.includes('auth.noactiveprofile') ||  // Daemon error code
        lower.includes('auth.profilenotfound')  // Daemon error code
    );
}

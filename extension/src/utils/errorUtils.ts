/**
 * Detect authentication errors from daemon RPC responses.
 * Uses specific patterns to avoid false positives from field names
 * like "author" or "token" in query results.
 *
 * Note: The 401 check uses a word-boundary regex to avoid matching substrings
 * like port numbers or IDs (e.g., "port 4011", "id: 40123"). Long-term the
 * daemon should return structured error codes instead of relying on message
 * heuristics.
 */
export function isAuthError(error: unknown): boolean {
    const msg = error instanceof Error ? error.message : String(error);
    const lower = msg.toLowerCase();
    return (
        lower.includes('unauthorized') ||
        /\b401\b/.test(lower) ||  // word-boundary match to avoid false positives on port numbers / IDs
        lower.includes('authentication failed') ||
        lower.includes('token expired') ||
        lower.includes('token has expired') ||
        lower.includes('invalid_grant') ||
        lower.includes('aadsts') ||  // Azure AD error codes
        lower.includes('auth.noactiveprofile') ||  // Daemon error code
        lower.includes('auth.profilenotfound')  // Daemon error code
    );
}

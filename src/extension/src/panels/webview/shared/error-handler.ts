/**
 * Shared error tracking for webview scripts.
 * Sets up window.onerror to record errors and forward them to the extension host.
 *
 * Call at the top of each webview script, right after imports.
 * Safe in IIFE bundles (esbuild) where import resolution is synchronous.
 */

declare global {
    interface Window {
        __ppds_errors: { msg: string; src: string; line: number | null; col: number | null; stack: string }[];
    }
}

export function installErrorHandler(
    postMessage: (msg: { command: 'webviewError'; error: string; stack?: string }) => void,
): void {
    window.__ppds_errors = [];
    window.onerror = function (msg, src, line, col, err) {
        const entry = {
            msg: String(msg),
            src: String(src ?? ''),
            line: line ?? null,
            col: col ?? null,
            stack: err?.stack ? err.stack.substring(0, 500) : '',
        };
        window.__ppds_errors.push(entry);
        postMessage({
            command: 'webviewError',
            error: entry.msg,
            stack: entry.stack || undefined,
        });
    };
}

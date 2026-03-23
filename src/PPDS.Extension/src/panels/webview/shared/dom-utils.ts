/**
 * Shared DOM utility functions for webview scripts.
 * These replace duplicated helpers in query-panel-webview.js and solutions-panel-webview.js.
 */

/** Escape a value for safe insertion as HTML text content. */
export function escapeHtml(str: unknown): string {
    if (str === null || str === undefined) return '';
    const s = String(str);
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

/** Escape a value for safe use inside an HTML attribute value (double-quoted). */
export function escapeAttr(str: unknown): string {
    if (str === null || str === undefined) return '';
    return String(str).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** Escape a value for safe use as a CSS identifier (wraps CSS.escape). */
export function cssEscape(str: unknown): string {
    if (str === null || str === undefined) return '';
    return CSS.escape(String(str));
}

/**
 * Format an ISO date string into a human-readable local date (date only, no time).
 * Returns empty string for falsy input; returns the raw string if parsing fails.
 */
export function formatDate(isoString: string | null | undefined): string {
    if (!isoString) return '';
    try {
        return new Date(isoString).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    } catch {
        return isoString;
    }
}

/**
 * Format an ISO date string into a human-readable local date and time (no seconds).
 * Returns empty string for falsy input; returns the raw string if parsing fails.
 * Example output: "Dec 11, 2025, 4:00 AM"
 */
export function formatDateTime(isoString: string | null | undefined): string {
    if (!isoString) return '';
    try {
        return new Date(isoString).toLocaleString(undefined, {
            year: 'numeric', month: 'short', day: 'numeric',
            hour: '2-digit', minute: '2-digit',
        });
    } catch {
        return isoString;
    }
}

/** Strip tabs and newlines from a cell value (used when building TSV/CSV output). */
export function sanitizeValue(val: string): string {
    return val.replace(/\t/g, ' ').replace(/\r?\n/g, ' ');
}

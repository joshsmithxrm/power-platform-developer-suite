import * as vscode from 'vscode';

export const WEB_RESOURCE_SCHEME = 'ppds-webresource';

export type WebResourceContentMode =
    | 'unpublished'     // Default — latest saved (editable)
    | 'published'       // Currently published (read-only, for diff)
    | 'server-current'  // Fresh fetch bypassing cache (for conflict diff)
    | 'local-pending';  // Stored pending save content (for conflict diff)

export interface ParsedWebResourceUri {
    environmentId: string;
    webResourceId: string;
    filename: string;
    mode: WebResourceContentMode;
}

export function createWebResourceUri(
    environmentId: string,
    webResourceId: string,
    filename: string,
    mode: WebResourceContentMode = 'unpublished',
): vscode.Uri {
    const path = `/${environmentId}/${webResourceId}/${filename}`;
    const query = mode !== 'unpublished' ? `mode=${mode}` : '';
    return vscode.Uri.from({ scheme: WEB_RESOURCE_SCHEME, path, query });
}

export function parseWebResourceUri(uri: vscode.Uri): ParsedWebResourceUri {
    const parts = uri.path.split('/').filter(Boolean);
    if (parts.length < 3) {
        throw new Error(`Invalid web resource URI: ${uri.toString()}`);
    }
    const params = new URLSearchParams(uri.query);
    const VALID_MODES: ReadonlySet<string> = new Set(['unpublished', 'published', 'server-current', 'local-pending']);
    const rawMode = params.get('mode') ?? 'unpublished';
    if (!VALID_MODES.has(rawMode)) {
        throw new Error(`Invalid web resource content mode: ${rawMode}`);
    }
    const mode = rawMode as WebResourceContentMode;
    return {
        environmentId: parts[0],
        webResourceId: parts[1],
        filename: parts.slice(2).join('/'),
        mode,
    };
}

export function getLanguageId(webResourceType: number): string | undefined {
    switch (webResourceType) {
        case 1: return 'html';
        case 2: return 'css';
        case 3: return 'javascript';
        case 4: return 'xml';
        case 9: return 'xsl';
        case 11: return 'xml'; // SVG
        case 12: return 'xml'; // RESX
        default: return undefined;
    }
}

/** Binary types that cannot be edited. */
export function isBinaryType(webResourceType: number): boolean {
    return [5, 6, 7, 8, 10].includes(webResourceType);
}

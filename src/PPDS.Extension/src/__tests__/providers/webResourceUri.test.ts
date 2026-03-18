import { describe, it, expect, vi } from 'vitest';

// ── Mock: vscode ────────────────────────────────────────────────────────────

vi.mock('vscode', () => ({
    Uri: {
        from(components: { scheme: string; path: string; query: string }): {
            scheme: string;
            path: string;
            query: string;
            toString(): string;
        } {
            return {
                scheme: components.scheme,
                path: components.path,
                query: components.query,
                toString() {
                    const q = this.query ? `?${this.query}` : '';
                    return `${this.scheme}://${this.path}${q}`;
                },
            };
        },
    },
}));

// ── Import after mocks ──────────────────────────────────────────────────────

import {
    WEB_RESOURCE_SCHEME,
    createWebResourceUri,
    parseWebResourceUri,
    getLanguageId,
    isBinaryType,
} from '../../providers/webResourceUri.js';

// ── createWebResourceUri ────────────────────────────────────────────────────

describe('createWebResourceUri', () => {
    it('builds correct URI for unpublished mode (default)', () => {
        const uri = createWebResourceUri('env-123', 'wr-456', 'new_script.js');
        expect(uri.scheme).toBe(WEB_RESOURCE_SCHEME);
        expect(uri.path).toBe('/env-123/wr-456/new_script.js');
        expect(uri.query).toBe('');
    });

    it('builds correct URI with explicit published mode', () => {
        const uri = createWebResourceUri('env-123', 'wr-456', 'style.css', 'published');
        expect(uri.scheme).toBe(WEB_RESOURCE_SCHEME);
        expect(uri.path).toBe('/env-123/wr-456/style.css');
        expect(uri.query).toBe('mode=published');
    });

    it('builds correct URI with server-current mode', () => {
        const uri = createWebResourceUri('env-123', 'wr-456', 'page.html', 'server-current');
        expect(uri.query).toBe('mode=server-current');
    });

    it('builds correct URI with local-pending mode', () => {
        const uri = createWebResourceUri('env-123', 'wr-456', 'page.html', 'local-pending');
        expect(uri.query).toBe('mode=local-pending');
    });

    it('omits query string for unpublished mode', () => {
        const uri = createWebResourceUri('env-123', 'wr-456', 'file.js', 'unpublished');
        expect(uri.query).toBe('');
    });
});

// ── parseWebResourceUri ─────────────────────────────────────────────────────

describe('parseWebResourceUri', () => {
    it('extracts all components from a simple URI', () => {
        const uri = createWebResourceUri('env-abc', 'wr-def', 'myscript.js');
        const parsed = parseWebResourceUri(uri as any);
        expect(parsed.environmentId).toBe('env-abc');
        expect(parsed.webResourceId).toBe('wr-def');
        expect(parsed.filename).toBe('myscript.js');
        expect(parsed.mode).toBe('unpublished');
    });

    it('handles nested filenames with slashes', () => {
        const uri = createWebResourceUri('env-1', 'wr-2', 'scripts/subfolder/app.js');
        const parsed = parseWebResourceUri(uri as any);
        expect(parsed.filename).toBe('scripts/subfolder/app.js');
    });

    it('defaults to unpublished mode when no query', () => {
        const uri = createWebResourceUri('env-1', 'wr-2', 'file.ts');
        const parsed = parseWebResourceUri(uri as any);
        expect(parsed.mode).toBe('unpublished');
    });

    it('extracts published mode from query', () => {
        const uri = createWebResourceUri('env-1', 'wr-2', 'file.ts', 'published');
        const parsed = parseWebResourceUri(uri as any);
        expect(parsed.mode).toBe('published');
    });

    it('extracts server-current mode from query', () => {
        const uri = createWebResourceUri('env-1', 'wr-2', 'file.ts', 'server-current');
        const parsed = parseWebResourceUri(uri as any);
        expect(parsed.mode).toBe('server-current');
    });

    it('extracts local-pending mode from query', () => {
        const uri = createWebResourceUri('env-1', 'wr-2', 'file.ts', 'local-pending');
        const parsed = parseWebResourceUri(uri as any);
        expect(parsed.mode).toBe('local-pending');
    });

    it('throws for invalid URI with fewer than 3 path parts', () => {
        const badUri = { path: '/only-one', query: '', toString: () => 'bad-uri' };
        expect(() => parseWebResourceUri(badUri as any)).toThrow('Invalid web resource URI');
    });

    it('throws for empty path', () => {
        const badUri = { path: '', query: '', toString: () => 'empty-uri' };
        expect(() => parseWebResourceUri(badUri as any)).toThrow('Invalid web resource URI');
    });

    it('throws for path with only two segments', () => {
        const badUri = { path: '/env/wr', query: '', toString: () => 'two-segments' };
        expect(() => parseWebResourceUri(badUri as any)).toThrow('Invalid web resource URI');
    });
});

// ── getLanguageId ───────────────────────────────────────────────────────────

describe('getLanguageId', () => {
    it('maps type 1 to html', () => {
        expect(getLanguageId(1)).toBe('html');
    });

    it('maps type 2 to css', () => {
        expect(getLanguageId(2)).toBe('css');
    });

    it('maps type 3 to javascript', () => {
        expect(getLanguageId(3)).toBe('javascript');
    });

    it('maps type 4 to xml', () => {
        expect(getLanguageId(4)).toBe('xml');
    });

    it('maps type 9 (XSL) to xsl', () => {
        expect(getLanguageId(9)).toBe('xsl');
    });

    it('maps type 11 (SVG) to xml', () => {
        expect(getLanguageId(11)).toBe('xml');
    });

    it('maps type 12 (RESX) to xml', () => {
        expect(getLanguageId(12)).toBe('xml');
    });

    it('returns undefined for PNG (type 5)', () => {
        expect(getLanguageId(5)).toBeUndefined();
    });

    it('returns undefined for JPG (type 6)', () => {
        expect(getLanguageId(6)).toBeUndefined();
    });

    it('returns undefined for GIF (type 7)', () => {
        expect(getLanguageId(7)).toBeUndefined();
    });

    it('returns undefined for ICO (type 8)', () => {
        expect(getLanguageId(8)).toBeUndefined();
    });

    it('returns undefined for Silverlight (type 10)', () => {
        expect(getLanguageId(10)).toBeUndefined();
    });

    it('returns undefined for unknown type', () => {
        expect(getLanguageId(99)).toBeUndefined();
    });
});

// ── isBinaryType ────────────────────────────────────────────────────────────

describe('isBinaryType', () => {
    it('identifies PNG (type 5) as binary', () => {
        expect(isBinaryType(5)).toBe(true);
    });

    it('identifies JPG (type 6) as binary', () => {
        expect(isBinaryType(6)).toBe(true);
    });

    it('identifies GIF (type 7) as binary', () => {
        expect(isBinaryType(7)).toBe(true);
    });

    it('identifies ICO (type 8) as binary', () => {
        expect(isBinaryType(8)).toBe(true);
    });

    it('identifies Silverlight (type 10) as binary', () => {
        expect(isBinaryType(10)).toBe(true);
    });

    it('identifies HTML (type 1) as non-binary', () => {
        expect(isBinaryType(1)).toBe(false);
    });

    it('identifies CSS (type 2) as non-binary', () => {
        expect(isBinaryType(2)).toBe(false);
    });

    it('identifies JavaScript (type 3) as non-binary', () => {
        expect(isBinaryType(3)).toBe(false);
    });

    it('identifies XML (type 4) as non-binary', () => {
        expect(isBinaryType(4)).toBe(false);
    });

    it('identifies XSL (type 9) as non-binary', () => {
        expect(isBinaryType(9)).toBe(false);
    });

    it('identifies SVG (type 11) as non-binary', () => {
        expect(isBinaryType(11)).toBe(false);
    });

    it('identifies RESX (type 12) as non-binary', () => {
        expect(isBinaryType(12)).toBe(false);
    });

    it('identifies unknown type as non-binary', () => {
        expect(isBinaryType(99)).toBe(false);
    });
});

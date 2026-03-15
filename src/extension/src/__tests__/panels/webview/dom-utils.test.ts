import { describe, it, expect } from 'vitest';
import { escapeHtml, escapeAttr, sanitizeValue, formatDate } from '../../../panels/webview/shared/dom-utils.js';

describe('escapeHtml', () => {
    it('escapes angle brackets and quotes', () => {
        expect(escapeHtml('<script>alert("xss")</script>')).toBe(
            '&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;'
        );
    });
    it('returns empty string for null', () => {
        expect(escapeHtml(null)).toBe('');
    });
    it('returns empty string for undefined', () => {
        expect(escapeHtml(undefined)).toBe('');
    });
    it('converts non-string values to string', () => {
        expect(escapeHtml(42)).toBe('42');
    });
    it('escapes ampersands', () => {
        expect(escapeHtml('a & b')).toBe('a &amp; b');
    });
    it('escapes single quotes', () => {
        expect(escapeHtml("it's")).toBe('it&#039;s');
    });
});

describe('escapeAttr', () => {
    it('escapes double quotes for attribute context', () => {
        expect(escapeAttr('value "with" quotes')).toContain('&quot;');
    });
    it('returns empty string for null', () => {
        expect(escapeAttr(null)).toBe('');
    });
    it('returns empty string for undefined', () => {
        expect(escapeAttr(undefined)).toBe('');
    });
    it('escapes angle brackets', () => {
        expect(escapeAttr('<img>')).toBe('&lt;img&gt;');
    });
});

describe('sanitizeValue', () => {
    it('strips tabs', () => {
        expect(sanitizeValue('a\tb')).toBe('a b');
    });
    it('strips newlines', () => {
        expect(sanitizeValue('a\nb')).toBe('a b');
    });
    it('strips tabs and newlines', () => {
        expect(sanitizeValue('a\tb\nc')).toBe('a b c');
    });
    it('strips carriage-return newlines', () => {
        expect(sanitizeValue('a\r\nb')).toBe('a b');
    });
    it('leaves plain strings unchanged', () => {
        expect(sanitizeValue('hello world')).toBe('hello world');
    });
});

describe('formatDate', () => {
    it('returns empty string for null', () => {
        expect(formatDate(null)).toBe('');
    });
    it('returns empty string for undefined', () => {
        expect(formatDate(undefined)).toBe('');
    });
    it('returns empty string for empty string', () => {
        expect(formatDate('')).toBe('');
    });
    it('formats a valid ISO string and contains the year', () => {
        const result = formatDate('2026-01-15T00:00:00Z');
        expect(result).toContain('2026');
    });
    it('formats a valid ISO string and contains the day', () => {
        // Use midday UTC to avoid date rolling back in negative-offset timezones
        const result = formatDate('2026-01-15T12:00:00Z');
        expect(result).toContain('15');
    });
});

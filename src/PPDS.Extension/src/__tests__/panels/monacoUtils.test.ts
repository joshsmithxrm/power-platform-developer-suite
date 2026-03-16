import { describe, it, expect } from 'vitest';
import { detectLanguage, mapCompletionKind, mapCompletionItems } from '../../panels/monacoUtils.js';

describe('detectLanguage', () => {
    it('returns sql for plain SQL', () => {
        expect(detectLanguage('SELECT * FROM account')).toBe('sql');
    });

    it('returns xml for FetchXML', () => {
        expect(detectLanguage('<fetch><entity name="account"/></fetch>')).toBe('xml');
    });

    it('returns xml for FetchXML with xml declaration', () => {
        expect(detectLanguage('<?xml version="1.0"?><fetch/>')).toBe('xml');
    });

    it('handles leading whitespace before SQL', () => {
        expect(detectLanguage('  \n  SELECT 1')).toBe('sql');
    });

    it('handles leading whitespace before FetchXML', () => {
        expect(detectLanguage('  \n  <fetch/>')).toBe('xml');
    });

    it('returns sql for empty string', () => {
        expect(detectLanguage('')).toBe('sql');
    });

    it('returns sql for whitespace-only', () => {
        expect(detectLanguage('   \n  ')).toBe('sql');
    });
});

describe('mapCompletionKind', () => {
    it('maps entity to Class (5)', () => {
        expect(mapCompletionKind('entity')).toBe(5);
    });

    it('maps attribute to Field (3)', () => {
        expect(mapCompletionKind('attribute')).toBe(3);
    });

    it('maps keyword to Keyword (17)', () => {
        expect(mapCompletionKind('keyword')).toBe(17);
    });

    it('maps unknown to Text (18)', () => {
        expect(mapCompletionKind('something')).toBe(18);
    });
});

describe('mapCompletionItems', () => {
    it('maps daemon items to Monaco format', () => {
        const items = [
            { label: 'account', insertText: 'account', kind: 'entity', detail: 'Entity', description: null, sortOrder: 1 },
            { label: 'name', insertText: 'name', kind: 'attribute', detail: 'String', description: null, sortOrder: 10 },
        ];
        const result = mapCompletionItems(items);
        expect(result).toHaveLength(2);
        expect(result[0]).toEqual({ label: 'account', insertText: 'account', kind: 5, detail: 'Entity', sortText: '00001' });
        expect(result[1]).toEqual({ label: 'name', insertText: 'name', kind: 3, detail: 'String', sortText: '00010' });
    });

    it('handles null detail', () => {
        const items = [
            { label: 'SELECT', insertText: 'SELECT', kind: 'keyword', detail: null, description: null, sortOrder: 0 },
        ];
        const result = mapCompletionItems(items);
        expect(result[0].detail).toBe('');
    });

    it('handles empty array', () => {
        expect(mapCompletionItems([])).toEqual([]);
    });

    it('zero-pads sortText to 5 digits', () => {
        const items = [
            { label: 'x', insertText: 'x', kind: 'keyword', detail: null, description: null, sortOrder: 42 },
        ];
        expect(mapCompletionItems(items)[0].sortText).toBe('00042');
    });
});

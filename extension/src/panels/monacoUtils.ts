/**
 * Pure utility functions for Monaco Editor integration.
 * Extracted for testability — no Monaco dependency.
 * The webview IIFE has inline copies of detectLanguage and mapKind — keep in sync.
 */

/**
 * Detect whether content is SQL or FetchXML based on first non-whitespace character.
 * SQL never starts with '<'. FetchXML always starts with '<fetch' or '<?xml'.
 */
export function detectLanguage(content: string): 'sql' | 'xml' {
    const trimmed = content.trimStart();
    return trimmed.startsWith('<') ? 'xml' : 'sql';
}

/**
 * Map daemon completion item kind to Monaco CompletionItemKind numeric value.
 * Values match monaco.languages.CompletionItemKind enum.
 */
export function mapCompletionKind(daemonKind: string): number {
    switch (daemonKind) {
        case 'entity': return 5;    // Class
        case 'attribute': return 3; // Field
        case 'keyword': return 17;  // Keyword
        default: return 18;         // Text
    }
}

/**
 * Map daemon CompletionItemDto array to Monaco-compatible suggestion shapes.
 */
export function mapCompletionItems(
    items: Array<{ label: string; insertText: string; kind: string; detail: string | null; description: string | null; sortOrder: number }>,
): Array<{ label: string; insertText: string; kind: number; detail: string; sortText: string }> {
    return items.map(item => ({
        label: item.label,
        insertText: item.insertText,
        kind: mapCompletionKind(item.kind),
        detail: item.detail ?? '',
        sortText: String(item.sortOrder).padStart(5, '0'),
    }));
}

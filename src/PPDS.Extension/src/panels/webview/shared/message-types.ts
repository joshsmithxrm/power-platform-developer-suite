/**
 * Typed message protocols for VS Code webview panels.
 * These discriminated unions are shared between the host (TypeScript, Node.js)
 * and webview (TypeScript, browser) to ensure type-safe communication.
 */

// Import daemon response types that appear in message payloads
import type { QueryResultResponse, CompletionItemDto } from '../../../types.js';

// ── Query Panel ─────────────────────────────────────────────────────────────

/** Messages the Query Panel webview sends to the extension host. */
export type QueryPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'executeQuery'; sql: string; useTds?: boolean; language?: string }
    | { command: 'showFetchXml'; sql: string }
    | { command: 'loadMore'; pagingCookie: string; page: number }
    | { command: 'explainQuery'; sql: string }
    | { command: 'exportResults'; format?: string }
    | { command: 'saveQuery'; sql: string; language: string }
    | { command: 'loadQueryFromFile' }
    | { command: 'openInNotebook'; sql: string }
    | { command: 'showHistory' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openRecordUrl'; url: string }
    | { command: 'requestClipboard' }
    | { command: 'requestCompletions'; requestId: number; sql: string; cursorOffset: number; language: string }
    | { command: 'webviewError'; error: string; stack?: string }
    | { command: 'cancelQuery' }
    | { command: 'convertQuery'; sql: string; fromLanguage: string; toLanguage: string }
    | { command: 'refresh' }
    | { command: 'requestEnvironmentList' };

/** Messages the extension host sends to the Query Panel webview. */
export type QueryPanelHostToWebview =
    | { command: 'loadQuery'; sql: string }
    | { command: 'updateEnvironment'; name: string; url: string | null; envType: string | null; envColor: string | null }
    | { command: 'executionStarted' }
    | { command: 'queryResult'; data: QueryResultResponse }
    | { command: 'queryCancelled' }
    | { command: 'queryError'; error: string }
    | { command: 'appendResults'; data: QueryResultResponse }
    | { command: 'clipboardContent'; text: string }
    | { command: 'completionResult'; requestId: number; items: CompletionItemDto[] }
    | { command: 'daemonReconnected' }
    | { command: 'queryConverted'; content: string; language: string }
    | { command: 'conversionFailed'; error: string; language: string };

// ── Solutions Panel ─────────────────────────────────────────────────────────

/** Serialized solution data sent to the webview. */
export interface SolutionViewDto {
    id: string;
    uniqueName: string;
    friendlyName: string;
    version: string;
    publisherName: string;
    isManaged: boolean;
    description: string;
    createdOn: string | null;
    modifiedOn: string | null;
    installedOn: string | null;
}

/** Component group sent after expanding a solution. */
export interface ComponentGroupDto {
    typeName: string;
    components: {
        objectId: string;
        isMetadata: boolean;
        logicalName?: string;
        schemaName?: string;
        displayName?: string;
        rootComponentBehavior: number;
    }[];
}

/** Messages the Solutions Panel webview sends to the extension host. */
export type SolutionsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'requestEnvironmentList' }
    | { command: 'refresh' }
    | { command: 'expandSolution'; uniqueName: string }
    | { command: 'collapseSolution'; uniqueName: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'openInMaker'; solutionId?: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Solutions Panel webview. */
export type SolutionsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'solutionsLoaded'; solutions: SolutionViewDto[] }
    | { command: 'componentsLoading'; uniqueName: string }
    | { command: 'componentsLoaded'; uniqueName: string; groups: ComponentGroupDto[] }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

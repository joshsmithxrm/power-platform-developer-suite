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

// ── Import Jobs Panel ─────────────────────────────────────────────────────

/** Import job info as sent to the webview for table display. */
export interface ImportJobViewDto {
    id: string;
    solutionName: string | null;
    status: string;
    progress: number;
    createdBy: string | null;
    createdOn: string | null;
    startedOn: string | null;
    completedOn: string | null;
    duration: string | null;
}

/** Messages the Import Jobs Panel webview sends to the extension host. */
export type ImportJobsPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectJob'; id: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Import Jobs Panel webview. */
export type ImportJobsPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'importJobsLoaded'; jobs: ImportJobViewDto[] }
    | { command: 'importJobDetailLoaded'; id: string; data: string | null }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };

// ── Plugin Traces Panel ──────────────────────────────────────────────────

/** Plugin trace info as sent to the webview for table display. */
export interface PluginTraceViewDto {
    id: string;
    typeName: string;
    messageName: string | null;
    primaryEntity: string | null;
    mode: string;
    operationType: string;
    depth: number;
    createdOn: string;
    durationMs: number | null;
    hasException: boolean;
    correlationId: string | null;
}

/** Plugin trace detail for the detail pane. */
export interface PluginTraceDetailViewDto extends PluginTraceViewDto {
    constructorDurationMs: number | null;
    executionStartTime: string | null;
    exceptionDetails: string | null;
    messageBlock: string | null;
    configuration: string | null;
    secureConfiguration: string | null;
    requestId: string | null;
}

/** Timeline node for the waterfall visualization. */
export interface TimelineNodeViewDto {
    traceId: string;
    typeName: string;
    messageName: string | null;
    depth: number;
    durationMs: number | null;
    hasException: boolean;
    offsetPercent: number;
    widthPercent: number;
    hierarchyDepth: number;
    children: TimelineNodeViewDto[];
}

/** Filter state sent from webview to host. */
export interface TraceFilterViewDto {
    typeName?: string;
    messageName?: string;
    primaryEntity?: string;
    mode?: string;
    hasException?: boolean;
    correlationId?: string;
    minDurationMs?: number;
    startDate?: string;
    endDate?: string;
}

/** Messages the Plugin Traces Panel webview sends to the extension host. */
export type PluginTracesPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'applyFilter'; filter: TraceFilterViewDto }
    | { command: 'selectTrace'; id: string }
    | { command: 'loadTimeline'; correlationId: string }
    | { command: 'deleteTraces'; ids: string[] }
    | { command: 'deleteOlderThan'; days: number }
    | { command: 'requestTraceLevel' }
    | { command: 'setTraceLevel'; level: string }
    | { command: 'setAutoRefresh'; intervalSeconds: number | null }
    | { command: 'requestEnvironmentList' }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Plugin Traces Panel webview. */
export type PluginTracesPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'tracesLoaded'; traces: PluginTraceViewDto[] }
    | { command: 'traceDetailLoaded'; trace: PluginTraceDetailViewDto }
    | { command: 'timelineLoaded'; nodes: TimelineNodeViewDto[] }
    | { command: 'traceLevelLoaded'; level: string; levelValue: number }
    | { command: 'deleteComplete'; deletedCount: number }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
